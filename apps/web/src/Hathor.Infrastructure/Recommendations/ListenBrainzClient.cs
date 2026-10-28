using System.Globalization;
using System.Text;
using System.Text.Json;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// ListenBrainz Labs public similarity client. We send only seed MBIDs; no
// user token, no listening history, no profile. Similarity scores are used
// as one feature by our local ranker — never as the final recommendation.
public sealed class ListenBrainzClient(
    IHttpClientFactory httpFactory,
    IRecommendationCache cache,
    ListenBrainzRateLimiter limiter,
    DiscoveryOptions options,
    ILogger<ListenBrainzClient> log) : IListenBrainzClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<SimilarArtistDto>> GetSimilarArtistsAsync(
        string artistMbid, int limit, CancellationToken ct = default)
    {
        if (!options.ListenBrainzEnabled || string.IsNullOrWhiteSpace(artistMbid))
            return [];

        limit = Math.Clamp(limit, 1, 100);
        var key = $"lb:similar-artists:{artistMbid}:{limit}";
        var cached = await cache.GetAsync<List<SimilarArtistDto>>(key, ct);
        if (cached is not null) return cached;

        var url = "similar-artists/json?" +
            $"artist_mbids={Uri.EscapeDataString(artistMbid)}" +
            $"&algorithm={Uri.EscapeDataString(options.ListenBrainzSimilarArtistsAlgorithm)}";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return [];

        var out_ = new List<(SimilarArtistDto Dto, double Raw)>();
        try
        {
            var array = FindArray(result.RootElement,
                "artists", "similar_artists", "results", "payload");
            if (array is not null)
            {
                foreach (var el in array.Value.EnumerateArray())
                {
                    var mbid = Str(el, "artist_mbid") ?? Str(el, "mbid") ?? Str(el, "id");
                    if (string.IsNullOrWhiteSpace(mbid)) continue;
                    var name = Str(el, "name") ?? Str(el, "artist_name") ?? "";
                    var score = Num(el, "score") ?? Num(el, "similarity") ?? 0;
                    out_.Add((new SimilarArtistDto(mbid, name, score), score));
                    if (out_.Count >= limit) break;
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            result.Dispose();
        }

        var normalized = Normalize(out_);
        await TryCacheAsync(key, normalized, ct);
        return normalized;
    }

    public async Task<IReadOnlyList<SimilarRecordingDto>> GetSimilarRecordingsAsync(
        string recordingMbid, int limit, CancellationToken ct = default)
    {
        if (!options.ListenBrainzEnabled || string.IsNullOrWhiteSpace(recordingMbid))
            return [];

        limit = Math.Clamp(limit, 1, 100);
        var key = $"lb:similar-recordings:{recordingMbid}:{limit}";
        var cached = await cache.GetAsync<List<SimilarRecordingDto>>(key, ct);
        if (cached is not null) return cached;

        var url = "similar-recordings/json?" +
            $"recording_mbids={Uri.EscapeDataString(recordingMbid)}" +
            $"&algorithm={Uri.EscapeDataString(options.ListenBrainzSimilarRecordingsAlgorithm)}";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return [];

        var out_ = new List<(SimilarRecordingDto Dto, double Raw)>();
        try
        {
            var array = FindArray(result.RootElement,
                "recordings", "similar_recordings", "results", "payload");
            if (array is not null)
            {
                foreach (var el in array.Value.EnumerateArray())
                {
                    var mbid = Str(el, "recording_mbid") ?? Str(el, "mbid") ?? Str(el, "id");
                    if (string.IsNullOrWhiteSpace(mbid)) continue;
                    var score = Num(el, "score") ?? Num(el, "similarity") ?? 0;
                    var listens = (long)(Num(el, "total_listen_count") ??
                        Num(el, "listen_count") ?? Num(el, "listeners") ?? 0);
                    out_.Add((new SimilarRecordingDto(
                        mbid, score, Math.Max(0, listens)), score));
                    if (out_.Count >= limit) break;
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            result.Dispose();
        }

        var normalized = Normalize(out_);
        await TryCacheAsync(key, normalized, ct);
        return normalized;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetRecordingPopularityAsync(
        IReadOnlyList<string> recordingMbids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!options.ListenBrainzEnabled || recordingMbids.Count == 0)
            return result;

        var unique = recordingMbids
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var chunk in unique.Chunk(100))
        {
            if (ct.IsCancellationRequested) break;
            var doc = await PostJsonAsync("1/popularity/recording",
                new { recording_mbids = chunk }, "listenbrainz-api", ct);
            if (doc is null) continue;
            try
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var mbid = Str(item, "recording_mbid");
                    var count = Num(item, "total_listen_count");
                    if (!string.IsNullOrWhiteSpace(mbid) && count is > 0)
                        result[mbid!] = (long)count.Value;
                }
            }
            catch (JsonException)
            {
                // A malformed batch is simply skipped.
            }
            finally
            {
                doc.Dispose();
            }
        }
        return result;
    }

    private async Task<JsonDocument?> PostJsonAsync(
        string relativeUrl, object body, string clientName, CancellationToken ct)
    {
        try
        {
            return await limiter.RunAsync(async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                var http = httpFactory.CreateClient(clientName);
                using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
                req.Headers.UserAgent.ParseAdd("Hathor/1.0");
                req.Headers.Accept.ParseAdd("application/json");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var res = await http.SendAsync(req,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                res.EnsureSuccessStatusCode();
                await using var stream = await res.Content.ReadAsStreamAsync(timeout.Token);
                return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ListenBrainz request failed ({Url})", relativeUrl);
            return null;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            return await limiter.RunAsync(async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                var http = httpFactory.CreateClient("listenbrainz");
                using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                req.Headers.UserAgent.ParseAdd("Hathor/1.0");
                req.Headers.Accept.ParseAdd("application/json");
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                res.EnsureSuccessStatusCode();
                await using var stream = await res.Content.ReadAsStreamAsync(timeout.Token);
                return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ListenBrainz request failed ({Url})", relativeUrl);
            return null;
        }
    }

    private async Task TryCacheAsync<T>(string key, T value, CancellationToken ct)
        where T : class
    {
        try
        {
            await cache.SetAsync(key, value, TimeSpan.FromDays(Math.Max(1, options.HttpCacheDays)), ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Recommendation cache write failed for {Key}", key);
        }
    }

    private static JsonElement? FindArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Array) return value;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nested in names)
                        if (value.TryGetProperty(nested, out var arr) &&
                            arr.ValueKind == JsonValueKind.Array)
                            return arr;
                }
            }
        }
        return null;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    // Labs similarity scores are relative integers (e.g. 7781), not 0..1.
    // Normalize by the largest score in the same response so the ranker sees
    // a stable 0..1 feature without assuming a provider-specific scale.
    private static List<SimilarArtistDto> Normalize(
        List<(SimilarArtistDto Dto, double Raw)> values)
    {
        var max = values.Count > 0 ? values.Max(v => v.Raw) : 0;
        var needsScale = values.Any(v => v.Raw > 1);
        return values.Select(v => v.Dto with
        {
            Score = needsScale && max > 0
                ? Math.Clamp(v.Raw / max, 0, 1)
                : Math.Clamp(v.Raw, 0, 1),
        }).ToList();
    }

    private static List<SimilarRecordingDto> Normalize(
        List<(SimilarRecordingDto Dto, double Raw)> values)
    {
        var max = values.Count > 0 ? values.Max(v => v.Raw) : 0;
        var needsScale = values.Any(v => v.Raw > 1);
        return values.Select(v => v.Dto with
        {
            Score = needsScale && max > 0
                ? Math.Clamp(v.Raw / max, 0, 1)
                : Math.Clamp(v.Raw, 0, 1),
        }).ToList();
    }
}

public sealed class ListenBrainzRateLimiter
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastUtc = DateTime.MinValue;

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = MinInterval - (DateTime.UtcNow - _lastUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try
            {
                return await action(ct);
            }
            finally
            {
                _lastUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
