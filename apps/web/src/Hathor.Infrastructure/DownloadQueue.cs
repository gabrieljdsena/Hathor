using System.Collections.Concurrent;
using Hathor.Application.Ingest;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Ingest;

// Bounded-concurrency download pump (desktop DownloadManager + Android
// QueueRepository semantics): persisted job rows, in-flight URL dedupe,
// stuck-heal on submit, user cancel (queued instant, running via CTS),
// 5-minute silence watchdog, SignalR staged progress.
public sealed class DownloadQueueService(
    IServiceScopeFactory scopes,
    IDownloadEngine engine,
    IConfiguration config,
    ILogger<DownloadQueueService> log) : IDownloadQueue, IDisposable
{
    private readonly SemaphoreSlim _pumpLock = new(1, 1);
    // Serializes resolve + in-flight check + insert in SubmitAsync: without
    // it two rapid submits (double-click, retry) both pass the
    // ActiveByUrlAsync check before either inserts, producing twin jobs
    // that land as "Title.mp3" + "Title (1).mp3" seconds apart.
    private readonly SemaphoreSlim _submitLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (Guid UserId, CancellationTokenSource Cts)> _running = new();
    private readonly HashSet<string> _userCancelled = [];
    private readonly object _cancelLock = new();
    private bool _disposed;

    private int MaxConcurrency => Math.Clamp(config.GetValue("Downloads:MaxConcurrency", 3), 1, 20);

    public async Task<string> SubmitAsync(Guid userId, string url, string title,
        string? artist, bool isPodcast, CancellationToken ct = default, string? targetFile = null)
    {
        using var scope = scopes.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();

        // Heal jobs stuck 'downloading' by a killed process (Android resetStuck).
        if (await jobs.ResetStuckAsync(userId, ct) > 0)
            await jobs.SaveChangesAsync(ct);

        var target = url.Trim();
        string resolvedTitle = title;
        await _submitLock.WaitAsync(ct);
        try
        {
            if (!IsDownloadableUrl(target))
            {
                var query = string.IsNullOrWhiteSpace(target)
                    ? $"{title} {artist} audio".Trim()
                    : target;
                if (string.IsNullOrWhiteSpace(query))
                    throw new InvalidOperationException("Download failed: empty search or url");
                var hits = await engine.SearchAsync(query, 1, ct);
                var hit = hits.FirstOrDefault()
                    ?? throw new InvalidOperationException($"No results found for: {query}");
                target = $"https://www.youtube.com/watch?v={hit.Id}";
                if (string.IsNullOrWhiteSpace(resolvedTitle) || resolvedTitle == url)
                    resolvedTitle = hit.Title;
                artist ??= hit.Uploader;
            }

            // Dedupe in-flight URLs; completed jobs do NOT block re-downloads.
            var existing = await jobs.ActiveByUrlAsync(userId, target, ct);
            if (existing is not null) return existing.Qid;

            var job = new Domain.Entities.DownloadJob
            {
                UserId = userId,
                Qid = Guid.NewGuid().ToString("N"),
                Url = target,
                Title = string.IsNullOrWhiteSpace(resolvedTitle) ? target : resolvedTitle,
                Artist = artist,
                Status = "queued",
                IsPodcast = isPodcast,
                TargetFile = string.IsNullOrWhiteSpace(targetFile)
                    ? null
                    : Path.GetFileName(targetFile.Trim()),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            await jobs.AddAsync(job, ct);
            await jobs.SaveChangesAsync(ct);
            Kick();
            return job.Qid;
        }
        finally
        {
            _submitLock.Release();
        }
    }

    public async Task<bool> RetryAsync(Guid userId, string qid, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
        var job = await jobs.GetByQidAsync(qid, ct);
        if (job is null || job.UserId != userId) return false;
        var target = (job.Url ?? "").Trim();
        if (string.IsNullOrEmpty(target) && string.IsNullOrWhiteSpace(job.Title)) return false;
        job.Status = "queued";
        job.Progress = 0;
        job.Error = null;
        job.Filename = null;
        job.UpdatedAtUtc = DateTime.UtcNow;
        await jobs.SaveChangesAsync(ct);
        Kick();
        return true;
    }

    public async Task<bool> CancelAsync(Guid userId, string qid, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
        var hub = scope.ServiceProvider.GetRequiredService<IPlaybackHub>();
        var job = await jobs.GetByQidAsync(qid, ct);
        if (job is null || job.UserId != userId) return false;
        if (job.Status == "queued")
        {
            job.Status = "cancelled";
            job.Error = "Cancelled by user";
            job.UpdatedAtUtc = DateTime.UtcNow;
            await jobs.SaveChangesAsync(ct);
            await hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job), ct);
            return true;
        }
        if (job.Status == "downloading" && _running.TryGetValue(qid, out var run) && run.UserId == userId)
        {
            lock (_cancelLock) _userCancelled.Add(qid);
            try { run.Cts.Cancel(); } catch { }
            return true;
        }
        return false;
    }

    public void Kick()
    {
        if (_disposed) return;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        await _pumpLock.WaitAsync();
        try
        {
            while (_running.Count < MaxConcurrency)
            {
                string? claimed = null;
                using (var scope = scopes.CreateScope())
                {
                    var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
                    // Oldest queued first (any user — global bound, like desktop).
                    // NOTE: cross-user fairness is best-effort in Phase 4.
                    var next = await jobs.ClaimNextAsync();
                    if (next is null) break;
                    await jobs.SaveChangesAsync();
                    claimed = next.Qid;
                }
                if (claimed is null) break;
                _ = RunJobAsync(claimed);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download pump failed");
        }
        finally
        {
            _pumpLock.Release();
        }
    }

    private async Task RunJobAsync(string qid)
    {
        using var scope = scopes.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
        var records = scope.ServiceProvider.GetRequiredService<ISongRecordRepository>();
        var podcasts = scope.ServiceProvider.GetRequiredService<IPodcastRecordRepository>();
        var writer = scope.ServiceProvider.GetRequiredService<IMetadataWriter>();
        var storage = scope.ServiceProvider.GetRequiredService<ILibraryStorage>();
        var metaReader = scope.ServiceProvider.GetRequiredService<Library.SongMetadataReader>();
        var itunes = scope.ServiceProvider.GetRequiredService<IITunesClient>();
        var loudness = scope.ServiceProvider.GetRequiredService<ILoudnessAnalyzer>();
        var hub = scope.ServiceProvider.GetRequiredService<IPlaybackHub>();

        var job = await jobs.GetByQidAsync(qid);
        if (job is null || job.Status == "cancelled") return;
        var userId = job.UserId;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _running[qid] = (userId, cts);
        var sync = new object();
        var lastActivity = DateTime.UtcNow;
        var lastPush = DateTime.MinValue;
        var lastVal = -1.0;
        var lastPhase = "starting";
        var watchdogFired = false;
        void Touch(double v) { lock (sync) { lastActivity = DateTime.UtcNow; lastVal = v; } }

        using var watchCts = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            try
            {
                while (!watchCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), watchCts.Token);
                    lock (sync)
                    {
                        if (DateTime.UtcNow - lastActivity > TimeSpan.FromMinutes(5))
                        {
                            watchdogFired = true;
                            try { cts.Cancel(); } catch { }
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // NOTE: the progress callback runs on the downloader thread while the
        // main flow is suspended in await — it only touches in-memory fields
        // and the thread-safe SignalR client. All DB writes stay on the main
        // flow (DbContext is not thread-safe).
        void ReportLive()
        {
            _ = hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job));
        }

        try
        {
            job.Status = "downloading";
            job.Progress = 0;
            await jobs.SaveChangesAsync();
            await hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job));

            var target = (job.Url ?? "").Trim();
            var info = await engine.GetInfoAsync(target, cts.Token);
            var title = !string.IsNullOrWhiteSpace(job.Title) && job.Title != job.Url
                ? job.Title : info.Title;
            var artist = job.Artist ?? info.Uploader;

            var tmpDir = Path.Combine(Path.GetTempPath(), "hathor-dl");
            var tmpMp3 = Path.Combine(tmpDir, qid + ".mp3");
            await engine.DownloadAudioAsync(target, tmpMp3, frac =>
            {
                // Desktop scales download progress into 0..90%.
                var scaled = Math.Clamp(frac, 0, 1) * 0.9;
                bool push;
                lock (sync)
                {
                    lastActivity = DateTime.UtcNow;
                    push = scaled - lastVal >= 0.05 ||
                        DateTime.UtcNow - lastPush > TimeSpan.FromSeconds(2);
                    if (push)
                    {
                        lastVal = scaled;
                        lastPush = DateTime.UtcNow;
                        job.Progress = scaled;
                    }
                }
                if (push) ReportLive();
            },
            // Engine phase transitions ("primary/manifest", "fallback/ytdlp",
            // …) prove liveness like progress does, and pin down the stall
            // point in failure messages below.
            phase =>
            {
                lock (sync)
                {
                    lastActivity = DateTime.UtcNow;
                    lastPhase = phase;
                }
            }, cts.Token);

            job.Progress = 0.9; // finished download, processing (desktop stages)
            await jobs.SaveChangesAsync();
            Touch(0.9);

            string finalTitle = title, finalArtist = artist ?? "Unknown";
            string? finalAlbum = null, finalYear = null, finalGenre = null;
            string? coverDataUrl = null;
            if (!job.IsPodcast)
            {
                var hit = await itunes.SearchSingleAsync(title, artist, cts.Token);
                if (hit is not null)
                {
                    finalTitle = hit.Title;
                    finalArtist = hit.Artist;
                    finalAlbum = hit.Album;
                    finalYear = hit.Year;
                    finalGenre = hit.Genre;
                    if (!string.IsNullOrWhiteSpace(hit.ArtworkUrl))
                    {
                        var bytes = await itunes.FetchArtworkAsync(hit.ArtworkUrl, cts.Token);
                        if (bytes is not null)
                            coverDataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
                    }
                }
                await writer.WritePathAsync(tmpMp3, finalTitle, finalArtist,
                    finalAlbum, finalYear, finalGenre, coverDataUrl, cts.Token);
            }
            // Podcasts skip the iTunes song-match (desktop): keep uploader title/author.
            job.Progress = 0.95;
            await jobs.SaveChangesAsync();
            Touch(0.95);

            var baseName = FileNamingService.SafeMp3FileName(finalTitle);
            string name;
            string finalPath;
            if (job.IsPodcast)
            {
                var existing = storage.ListPodcastFiles(userId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                name = FileNamingService.ResolveFinalName(job.TargetFile, baseName, existing.Contains);
                finalPath = storage.PodcastPath(userId, name);
                if (job.TargetFile is not null && File.Exists(finalPath))
                    try { File.Delete(tmpMp3); } catch { }
                else
                    File.Move(tmpMp3, finalPath);
                await podcasts.EnsureAsync(userId, name, title, artist, info.PageUrl);
                await podcasts.SaveChangesAsync();
            }
            else
            {
                var existing = storage.ListSongFiles(userId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                name = FileNamingService.ResolveFinalName(job.TargetFile, baseName, existing.Contains);
                finalPath = storage.SongPath(userId, name);
                if (job.TargetFile is not null && File.Exists(finalPath))
                    try { File.Delete(tmpMp3); } catch { }
                else
                    File.Move(tmpMp3, finalPath);
                // Materialize the finished file's tags so lists/search never
                // open it (single read, then DB only).
                var finished = metaReader.Read(userId, name, includeCover: false);
                await records.UpsertDownloadedAsync(userId, name, info.PageUrl,
                    finished.Title, finished.Artist, finished.Album,
                    finished.Year, finished.Genre, finished.Duration);
                // Measure loudness for per-track normalization (best effort:
                // analysis must never fail a completed download).
                try
                {
                    var lufs = await loudness.AnalyzeAsync(finalPath, cts.Token);
                    if (lufs is not null)
                        await records.SetLoudnessAsync(userId, name, lufs.Value, cts.Token);
                }
                catch { }
                await records.SaveChangesAsync();
            }

            job.Status = "done";
            job.Progress = 1.0;
            job.Filename = Path.GetFileName(finalPath);
            job.Error = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
            await jobs.SaveChangesAsync();
            await hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job));
            _ = CleanupTempAsync(tmpDir, qid);
        }
        catch (OperationCanceledException)
        {
            bool user;
            string phase;
            lock (_cancelLock) user = _userCancelled.Remove(qid);
            lock (sync) phase = lastPhase;
            job.Status = "cancelled";
            job.Error = user ? "Cancelled by user"
                : watchdogFired ? $"Stopped: no progress for 5 minutes (network/YouTube blocked? stalled in {phase})"
                : "Download cancelled";
            job.UpdatedAtUtc = DateTime.UtcNow;
            await jobs.SaveChangesAsync();
            await hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job));
        }
        catch (Exception ex)
        {
            // Error (not Warning): a failed job is user-visible and needs
            // operator attention — the lab Postgres sink only persists
            // Error/Fatal, so Warning here never reached the logs table.
            log.LogError(ex, "Download {Qid} failed", qid);
            string phase;
            lock (sync) phase = lastPhase;
            job.Status = "failed";
            job.Error = $"Download failed: {ex.Message} (phase: {phase})";
            job.UpdatedAtUtc = DateTime.UtcNow;
            await jobs.SaveChangesAsync();
            await hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job));
        }
        finally
        {
            _running.TryRemove(qid, out _);
            try { watchCts.Cancel(); } catch { }
            try { await watch; } catch { }
            job.UpdatedAtUtc = DateTime.UtcNow;
            await jobs.SaveChangesAsync();
            await hub.BroadcastDownloadAsync(userId, DownloadJobMaps.ToDto(job));
            Kick(); // pump the next queued job
        }
    }

    private static Task CleanupTempAsync(string tmpDir, string qid) => Task.Run(() =>
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(tmpDir, qid + ".*"))
                File.Delete(f);
        }
        catch { }
    });

    private static bool IsDownloadableUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _disposed = true;
        _pumpLock.Dispose();
        _submitLock.Dispose();
        foreach (var (_, run) in _running) try { run.Cts.Cancel(); } catch { }
    }
}
