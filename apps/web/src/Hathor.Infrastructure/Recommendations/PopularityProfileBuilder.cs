using System.Security.Cryptography;
using System.Text;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// User's popularity taste: resolve top played tracks to MBIDs, fetch global
// listen counts, and keep the median log10 count. Cached per user+track set
// so daily refreshes do not repeat MusicBrainz lookups.
public sealed class PopularityProfileBuilder(
    IMusicBrainzClient musicBrainz,
    ITrackPopularityService popularity,
    IRecommendationCache cache,
    DiscoveryOptions options,
    ILogger<PopularityProfileBuilder> log) : IPopularityProfileBuilder
{
    public async Task<PopularityProfile?> BuildAsync(
        Guid userId, IReadOnlyList<TasteTrack> tracks, CancellationToken ct = default)
    {
        var picked = tracks
            .Take(Math.Clamp(options.PopularityProfileSeeds, 1, 20))
            .ToList();
        if (picked.Count == 0) return null;

        var key = CacheKey(userId, picked);
        try
        {
            var cached = await cache.GetAsync<PopularityProfile>(key, ct);
            if (cached is not null) return cached;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug(ex, "Popularity profile cache read failed");
        }

        try
        {
            var mbids = new List<string>();
            foreach (var track in picked)
            {
                if (ct.IsCancellationRequested) break;
                var recording = await musicBrainz.SearchRecordingAsync(
                    track.Title, track.Artist, ct);
                if (recording is not null) mbids.Add(recording.Mbid);
            }
            var unique = mbids.Distinct(StringComparer.Ordinal).ToList();
            if (unique.Count < Math.Max(1, options.PopularityProfileMinSamples))
                return null;

            var counts = await popularity.GetAsync(unique, ct);
            var logs = counts.Values
                .Where(count => count > 0)
                .Select(count => Math.Log10(count))
                .OrderBy(value => value)
                .ToList();
            if (logs.Count < Math.Max(1, options.PopularityProfileMinSamples))
                return null;

            var mid = logs.Count / 2;
            var median = logs.Count % 2 == 1
                ? logs[mid]
                : (logs[mid - 1] + logs[mid]) / 2.0;
            var profile = new PopularityProfile(median, logs.Count);

            try
            {
                await cache.SetAsync(key, profile,
                    TimeSpan.FromDays(Math.Clamp(options.PopularityProfileCacheDays, 1, 90)),
                    ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(ex, "Popularity profile cache write failed");
            }
            return profile;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Popularity profile build failed");
            return null;
        }
    }

    private static string CacheKey(Guid userId, IReadOnlyList<TasteTrack> tracks)
    {
        var material = string.Join('\u0001',
            tracks.Select(t => t.Title + "\u0002" + t.Artist));
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return $"rec:pop-profile:{userId:N}:{hash}";
    }
}
