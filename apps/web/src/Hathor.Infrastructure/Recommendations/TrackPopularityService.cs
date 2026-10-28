using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// Reads global recording popularity from the local MBID cache, fetching only
// unknown recordings from ListenBrainz. Unknown counts are not cached so a
// later request can retry after LB backfills.
public sealed class TrackPopularityService(
    ITrackPopularityRepository cache,
    IListenBrainzClient listenBrainz,
    ILogger<TrackPopularityService> log) : ITrackPopularityService
{
    public async Task<IReadOnlyDictionary<string, long>> GetAsync(
        IReadOnlyList<string> recordingMbids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var unique = recordingMbids
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unique.Count == 0) return result;

        var cached = await cache.GetManyAsync(unique, ct);
        foreach (var (mbid, count) in cached) result[mbid] = count;

        var missing = unique.Where(m => !cached.ContainsKey(m)).ToList();
        if (missing.Count == 0) return result;

        var fetched = await listenBrainz.GetRecordingPopularityAsync(missing, ct);
        if (fetched.Count > 0)
        {
            try
            {
                await cache.UpsertAsync(fetched, ct);
                await cache.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(ex, "Popularity cache write failed");
            }
            foreach (var (mbid, count) in fetched) result[mbid] = count;
        }
        return result;
    }
}
