using System.Text.Json;
using System.Text.RegularExpressions;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Enrichment;

// lrclib.net client (desktop LyricsService fetch paths): exact /api/get,
// track-only fallback (exact case-insensitive non-instrumental match),
// /api/search suggestions (max 10 deduped, skip instrumentals/empty).
public sealed class LrclibClientImpl(IHttpClientFactory httpFactory, ILogger<LrclibClientImpl> log) : ILrclibClient
{
    private const string Base = "https://lrclib.net";

    public async Task<LyricsDto?> GetExactAsync(string track, string artist,
        int? durationSec, CancellationToken ct = default)
    {
        try
        {
            var url = $"{Base}/api/get?track_name={Esc(track)}&artist_name={Esc(artist)}" +
                (durationSec.HasValue ? $"&duration={durationSec.Value}" : "");
            var http = httpFactory.CreateClient("lrclib");
            using var res = await http.GetAsync(url, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            res.EnsureSuccessStatusCode();
            return ToDto(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "lrclib exact fetch failed for {Track}", track);
            return null;
        }
    }

    public async Task<LyricsDto?> SearchTrackOnlyAsync(string track,
        int? durationSec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track)) return null;
        try
        {
            var url = $"{Base}/api/search?track_name={Esc(track)}" +
                (durationSec.HasValue ? $"&duration={durationSec.Value}" : "");
            var http = httpFactory.CreateClient("lrclib");
            using var res = await http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var norm = Regex.Replace(track, @"\s+", " ").Trim().ToLowerInvariant();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!LyricsCleaning.AcceptTrackOnlyCandidate(
                    norm,
                    item.TryGetProperty("trackName", out var t) ? t.GetString() : null,
                    item.TryGetProperty("instrumental", out var ins) && ins.GetBoolean()))
                    continue;
                var dto = ToDto(item.GetRawText());
                if (dto?.Synced is null && dto?.Plain is null) return null;
                return dto;
            }
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "lrclib track-only search failed for {Track}", track);
            return null;
        }
    }

    public async Task<IReadOnlyList<LyricsHitDto>> SearchAsync(string track, string artist,
        string? album, int? durationSec, CancellationToken ct = default)
    {
        try
        {
            var url = $"{Base}/api/search?track_name={Esc(track)}&artist_name={Esc(artist)}" +
                (string.IsNullOrWhiteSpace(album) ? "" : $"&album_name={Esc(album)}") +
                (durationSec.HasValue ? $"&duration={durationSec.Value}" : "");
            var http = httpFactory.CreateClient("lrclib");
            using var res = await http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var out_ = new List<LyricsHitDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("instrumental", out var ins) && ins.GetBoolean()) continue;
                string? Str(string k) =>
                    item.TryGetProperty(k, out var v) ? v.GetString() : null;
                if (Str("syncedLyrics") is null && Str("plainLyrics") is null) continue;
                var key = $"{Str("trackName")}\n{Str("artistName")}";
                if (!seen.Add(key)) continue;
                out_.Add(new LyricsHitDto(
                    item.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                    Str("trackName") ?? "",
                    Str("artistName") ?? "",
                    Str("albumName"),
                    item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? d.GetDouble() : null,
                    Str("syncedLyrics"),
                    Str("plainLyrics")));
                if (out_.Count >= 10) break;
            }
            return out_;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "lrclib search failed for {Track}", track);
            return [];
        }
    }

    private static LyricsDto? ToDto(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string k) =>
                root.TryGetProperty(k, out var v) ? v.GetString() : null;
            var synced = Str("syncedLyrics");
            var plain = Str("plainLyrics");
            return synced is null && plain is null ? null : new LyricsDto(synced, plain);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);
}
