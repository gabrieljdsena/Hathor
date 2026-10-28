using System.Text.Json;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// MusicBrainz open-data client (CC0 core data): canonical MBIDs, recordings
// and the artist relationship graph. No user profile or audio is ever sent.
// MusicBrainz asks for <=1 request/second and a meaningful User-Agent; the
// singleton limiter + Postgres cache enforce both across the process.
public sealed class MusicBrainzClient(
    IHttpClientFactory httpFactory,
    IRecommendationCache cache,
    MusicBrainzRateLimiter limiter,
    DiscoveryOptions options,
    ILogger<MusicBrainzClient> log) : IMusicBrainzClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<MbArtistDto?> SearchArtistAsync(
        string name, CancellationToken ct = default)
    {
        if (!options.MusicBrainzEnabled || string.IsNullOrWhiteSpace(name)) return null;

        var key = "mb:artist:" + MediaKeys.Normalize(name);
        var cached = await cache.GetAsync<MbArtistDto>(key, ct);
        if (cached is not null) return cached;

        var query = $"artist:\"{Escape(name)}\"";
        var url = $"artist?query={Uri.EscapeDataString(query)}&fmt=json&limit=1";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return null;

        try
        {
            var root = result.RootElement;
            if (!root.TryGetProperty("artists", out var artists) ||
                artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() == 0)
                return null;

            var a = artists[0];
            var score = ParseDouble(a, "score");
            if (score < 70) return null; // avoid mismatched local tags

            var dto = new MbArtistDto(
                Str(a, "id") ?? "",
                Str(a, "name") ?? name,
                score,
                ReadTags(a));
            if (dto.Mbid.Length == 0) return null;

            await TryCacheAsync(key, dto, options.IdentityCacheDays, ct);
            return dto;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            result.Dispose();
        }
    }

    public async Task<MbRecordingDto?> SearchRecordingAsync(
        string title, string artist, CancellationToken ct = default)
    {
        if (!options.MusicBrainzEnabled || string.IsNullOrWhiteSpace(title)) return null;

        var key = "mb:recording-search:" + MediaKeys.Key(title, artist);
        var cached = await cache.GetAsync<MbRecordingDto>(key, ct);
        if (cached is not null) return cached;

        var query = string.IsNullOrWhiteSpace(artist)
            ? $"recording:\"{Escape(title)}\""
            : $"recording:\"{Escape(title)}\" AND artist:\"{Escape(artist)}\"";
        var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=1";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return null;

        try
        {
            var root = result.RootElement;
            if (!root.TryGetProperty("recordings", out var recordings) ||
                recordings.ValueKind != JsonValueKind.Array || recordings.GetArrayLength() == 0)
                return null;

            var score = ParseDouble(recordings[0], "score");
            if (score > 0 && score < 70) return null;

            var dto = ParseRecording(recordings[0]);
            if (dto is null) return null;

            await TryCacheAsync(key, dto, options.IdentityCacheDays, ct);
            return dto;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            result.Dispose();
        }
    }

    public async Task<MbRecordingDto?> GetRecordingAsync(
        string mbid, CancellationToken ct = default)
    {
        if (!options.MusicBrainzEnabled || string.IsNullOrWhiteSpace(mbid)) return null;

        var key = "mb:recording:" + mbid;
        var cached = await cache.GetAsync<MbRecordingDto>(key, ct);
        if (cached is not null) return cached;

        var url = $"recording/{Uri.EscapeDataString(mbid)}?inc=releases+tags+artist-credits&fmt=json";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return null;

        try
        {
            var dto = ParseRecording(result.RootElement);
            if (dto is not null)
                await TryCacheAsync(key, dto, options.IdentityCacheDays, ct);
            return dto;
        }
        finally
        {
            result.Dispose();
        }
    }

    public async Task<IReadOnlyList<MbRecordingDto>> GetArtistRecordingsAsync(
        string artistMbid, int limit, CancellationToken ct = default)
    {
        if (!options.MusicBrainzEnabled || string.IsNullOrWhiteSpace(artistMbid))
            return [];

        limit = Math.Clamp(limit, 1, 100);
        var key = $"mb:artist-recordings:{artistMbid}:{limit}";
        var cached = await cache.GetAsync<List<MbRecordingDto>>(key, ct);
        if (cached is not null) return cached;

        // Browse recordings rejects inc=releases; album/year come from the
        // lookup/search path or the iTunes resolver instead.
        var url = $"recording?artist={Uri.EscapeDataString(artistMbid)}" +
            $"&inc=artist-credits+tags&fmt=json&limit={limit}";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return [];

        var out_ = new List<MbRecordingDto>();
        try
        {
            if (result.RootElement.TryGetProperty("recordings", out var recordings) &&
                recordings.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in recordings.EnumerateArray())
                {
                    var dto = ParseRecording(item);
                    if (dto is not null) out_.Add(dto);
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

        await TryCacheAsync(key, out_, options.HttpCacheDays, ct);
        return out_;
    }

    public async Task<IReadOnlyList<MbArtistDto>> GetRelatedArtistsAsync(
        string artistMbid, int limit, CancellationToken ct = default)
    {
        if (!options.MusicBrainzEnabled || string.IsNullOrWhiteSpace(artistMbid))
            return [];

        limit = Math.Clamp(limit, 1, 100);
        var key = $"mb:artist-rels:{artistMbid}:{limit}";
        var cached = await cache.GetAsync<List<MbArtistDto>>(key, ct);
        if (cached is not null) return cached;

        var url = $"artist/{Uri.EscapeDataString(artistMbid)}" +
            "?inc=artist-rels+genres+tags&fmt=json";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return [];

        var out_ = new List<MbArtistDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (result.RootElement.TryGetProperty("relations", out var relations) &&
                relations.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in relations.EnumerateArray())
                {
                    if (!rel.TryGetProperty("artist", out var artist)) continue;
                    var mbid = Str(artist, "id");
                    var name = Str(artist, "name");
                    if (string.IsNullOrWhiteSpace(mbid) || string.IsNullOrWhiteSpace(name))
                        continue;
                    if (!seen.Add(mbid)) continue;
                    out_.Add(new MbArtistDto(mbid, name, 0.7, ReadTags(artist)));
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

        await TryCacheAsync(key, out_, options.HttpCacheDays, ct);
        return out_;
    }

    public async Task<IReadOnlyList<MbRecordingDto>> GetTagRecordingsAsync(
        string tag, int limit, CancellationToken ct = default)
    {
        if (!options.MusicBrainzEnabled || string.IsNullOrWhiteSpace(tag)) return [];

        limit = Math.Clamp(limit, 1, 100);
        var key = $"mb:tag-recordings:{MediaKeys.Normalize(tag)}:{limit}";
        var cached = await cache.GetAsync<List<MbRecordingDto>>(key, ct);
        if (cached is not null) return cached;

        var query = $"tag:\"{Escape(tag)}\"";
        var url = $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}";
        var result = await GetJsonAsync(url, ct);
        if (result is null) return [];

        var out_ = new List<MbRecordingDto>();
        try
        {
            if (result.RootElement.TryGetProperty("recordings", out var recordings) &&
                recordings.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in recordings.EnumerateArray())
                {
                    var dto = ParseRecording(item);
                    if (dto is not null) out_.Add(dto);
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

        await TryCacheAsync(key, out_, options.HttpCacheDays, ct);
        return out_;
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        try
        {
            return await limiter.RunAsync(async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);
                var http = httpFactory.CreateClient("musicbrainz");
                using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                req.Headers.UserAgent.ParseAdd(options.MusicBrainzUserAgent);
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
            log.LogWarning(ex, "MusicBrainz request failed ({Url})", relativeUrl);
            return null;
        }
    }

    private async Task TryCacheAsync<T>(string key, T value, int days, CancellationToken ct)
        where T : class
    {
        try
        {
            await cache.SetAsync(key, value, TimeSpan.FromDays(Math.Max(1, days)), ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Recommendation cache write failed for {Key}", key);
        }
    }

    private static MbRecordingDto? ParseRecording(JsonElement r)
    {
        var mbid = Str(r, "id");
        var title = Str(r, "title");
        if (string.IsNullOrWhiteSpace(mbid) || string.IsNullOrWhiteSpace(title))
            return null;

        var artist = "";
        if (r.TryGetProperty("artist-credit", out var credits) &&
            credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in credits.EnumerateArray())
            {
                artist = Str(credit, "name") ?? "";
                if (artist.Length > 0) break;
                if (credit.TryGetProperty("artist", out var a))
                {
                    artist = Str(a, "name") ?? "";
                    if (artist.Length > 0) break;
                }
            }
        }

        var album = "";
        var year = "";
        if (r.TryGetProperty("releases", out var releases) &&
            releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0)
        {
            var release = releases[0];
            album = Str(release, "title") ?? "";
            var date = Str(release, "date") ?? "";
            if (date.Length >= 4) year = date[..4];
        }
        if (year.Length == 0)
        {
            var first = Str(r, "first-release-date") ?? "";
            if (first.Length >= 4) year = first[..4];
        }

        var tags = ReadTags(r);
        return new MbRecordingDto(mbid, title, artist, album, year, tags);
    }

    private static IReadOnlyList<string> ReadTags(JsonElement el)
    {
        var tags = new List<string>();
        if (el.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var item in t.EnumerateArray())
            {
                var name = Str(item, "name");
                if (!string.IsNullOrWhiteSpace(name)) tags.Add(name);
            }
        if (el.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
            foreach (var item in g.EnumerateArray())
            {
                var name = Str(item, "name");
                if (!string.IsNullOrWhiteSpace(name) && !tags.Contains(name, StringComparer.OrdinalIgnoreCase))
                    tags.Add(name);
            }
        return tags;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double ParseDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
            _ => 0,
        };
    }

    // Lucene treats these as syntax; local tags often contain colons,
    // parentheses and dashes, so flatten them to spaces for safe queries.
    private static readonly char[] LuceneSpecial =
    [
        '\\', '"', ':', '(', ')', '[', ']', '{', '}', '^', '~', '?', '*', '|', '!', '+', '-',
    ];

    private static string Escape(string value) =>
        string.Join(' ', value.Split(LuceneSpecial, StringSplitOptions.RemoveEmptyEntries)).Trim();
}

// MusicBrainz asks for <=1 request/second. A single process-wide gate +
// last-request timestamp serializes all client calls, including parallel
// candidate-source fan-out.
public sealed class MusicBrainzRateLimiter
{
    // Slightly above MusicBrainz's 1 req/s guidance: bursts at exactly 1s
    // still trigger sporadic 503s on their public servers.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1200);
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
