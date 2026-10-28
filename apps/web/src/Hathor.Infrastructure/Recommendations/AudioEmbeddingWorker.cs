using System.Text.Json;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// Background CLAP backfill: local library tracks first, then 30s previews for
// candidates that recently appeared in Discover snapshots. Disabled by
// default; every failure is soft (metadata ranking keeps working).
public sealed class AudioEmbeddingWorker(
    IServiceScopeFactory scopes,
    DiscoveryOptions options,
    ILogger<AudioEmbeddingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.AudioEmbeddingsEnabled)
        {
            log.LogInformation("CLAP audio embeddings disabled");
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        var interval = TimeSpan.FromMinutes(
            Math.Clamp(options.AudioEmbeddingIntervalMinutes, 1, 1440));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "CLAP embedding cycle failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HathorDbContext>();
        var userIds = await db.Users.AsNoTracking()
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var userId in userIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await EmbedLibraryAsync(scope.ServiceProvider, userId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "CLAP library embedding failed for {UserId}", userId);
            }

            try
            {
                await EmbedPreviewsAsync(scope.ServiceProvider, userId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "CLAP preview embedding failed for {UserId}", userId);
            }
        }
    }

    private async Task EmbedLibraryAsync(
        IServiceProvider sp, Guid userId, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<ITrackEmbeddingRepository>();
        var storage = sp.GetRequiredService<ILibraryStorage>();
        var provider = sp.GetRequiredService<IEmbeddingProvider>();

        var files = await repo.GetMissingSongFilesAsync(
            userId, options.AudioEmbeddingModel,
            Math.Clamp(options.AudioEmbeddingBatchSize, 1, 32), ct);
        var paths = new List<(string File, string Path)>();
        foreach (var file in files)
        {
            try
            {
                if (!storage.SongExists(userId, file)) continue;
                var path = storage.SongPath(userId, file);
                if (File.Exists(path)) paths.Add((file, path));
            }
            catch
            {
                /* a missing/locked file is not a cycle failure */
            }
        }
        if (paths.Count == 0) return;

        var vectors = await provider.EmbedFilesAsync(
            paths.Select(p => p.Path).ToList(), ct);
        var now = DateTime.UtcNow;
        var saved = 0;
        for (var i = 0; i < paths.Count && i < vectors.Count; i++)
        {
            var vector = vectors[i];
            if (vector is not { Length: > 0 }) continue;
            await repo.SaveAsync(new TrackEmbedding
            {
                UserId = userId,
                OwnerType = "song",
                OwnerKey = paths[i].File,
                Model = options.AudioEmbeddingModel,
                VectorJson = JsonSerializer.Serialize(vector),
                Dimension = vector.Length,
                CreatedAtUtc = now,
            }, ct);
            saved++;
        }
        if (saved > 0)
        {
            await repo.SaveChangesAsync(ct);
            log.LogInformation("CLAP embedded {Count} library tracks for {UserId}", saved, userId);
        }
    }

    private async Task EmbedPreviewsAsync(
        IServiceProvider sp, Guid userId, CancellationToken ct)
    {
        var impressions = sp.GetRequiredService<IDiscoverImpressionRepository>();
        var repo = sp.GetRequiredService<ITrackEmbeddingRepository>();
        var itunes = sp.GetRequiredService<IITunesClient>();
        var provider = sp.GetRequiredService<IEmbeddingProvider>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7))
            .ToString("yyyy-MM-dd");
        var snapshots = await impressions.GetSinceAsync(userId, since, ct);
        var items = new List<DiscoverImpressionItem>();
        foreach (var snapshot in snapshots.OrderByDescending(s => s.Date).Take(2))
        {
            try
            {
                items.AddRange(JsonSerializer.Deserialize<List<DiscoverImpressionItem>>(
                    snapshot.ItemsJson) ?? []);
            }
            catch (JsonException)
            {
                /* skip a corrupt snapshot */
            }
        }

        var mbids = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Mbid))
            .Select(i => i.Mbid!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (mbids.Count == 0) return;

        var existing = (await repo.GetManyAsync(
                userId, "preview", mbids, options.AudioEmbeddingModel, ct))
            .Select(e => e.OwnerKey)
            .ToHashSet(StringComparer.Ordinal);
        var missing = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Mbid) &&
                        !existing.Contains(i.Mbid!))
            .GroupBy(i => i.Mbid!, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(Math.Clamp(options.AudioEmbeddingBatchSize, 1, 32))
            .ToList();
        if (missing.Count == 0) return;

        var temp = new List<(string Path, string Mbid)>();
        try
        {
            var http = httpFactory.CreateClient("preview");
            foreach (var item in missing)
            {
                if (ct.IsCancellationRequested) break;
                var hit = await itunes.SearchSingleAsync(item.Title, item.Artist, ct);
                if (hit is null || string.IsNullOrWhiteSpace(hit.PreviewUrl) ||
                    !MediaKeys.IsSameArtist(hit.Artist, item.Artist))
                    continue;

                var path = Path.Combine(Path.GetTempPath(),
                    $"hathor-preview-{Guid.NewGuid():N}.m4a");
                try
                {
                    using var res = await http.GetAsync(hit.PreviewUrl,
                        HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!res.IsSuccessStatusCode) continue;
                    if (res.Content.Headers.ContentLength is > 20_000_000) continue;
                    await using var stream = File.Create(path);
                    await res.Content.CopyToAsync(stream, ct);
                    temp.Add((path, item.Mbid!));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogDebug(ex, "Preview download failed for {Title}", item.Title);
                    try { File.Delete(path); } catch { /* best-effort */ }
                }
            }

            if (temp.Count == 0) return;
            var vectors = await provider.EmbedFilesAsync(
                temp.Select(t => t.Path).ToList(), ct);
            var now = DateTime.UtcNow;
            var saved = 0;
            for (var i = 0; i < temp.Count && i < vectors.Count; i++)
            {
                var vector = vectors[i];
                if (vector is not { Length: > 0 }) continue;
                await repo.SaveAsync(new TrackEmbedding
                {
                    UserId = userId,
                    OwnerType = "preview",
                    OwnerKey = temp[i].Mbid,
                    Model = options.AudioEmbeddingModel,
                    VectorJson = JsonSerializer.Serialize(vector),
                    Dimension = vector.Length,
                    CreatedAtUtc = now,
                }, ct);
                saved++;
            }
            if (saved > 0)
            {
                await repo.SaveChangesAsync(ct);
                log.LogInformation("CLAP embedded {Count} previews for {UserId}", saved, userId);
            }
        }
        finally
        {
            foreach (var (path, _) in temp)
            {
                try { File.Delete(path); } catch { /* temp cleanup */ }
            }
        }
    }
}
