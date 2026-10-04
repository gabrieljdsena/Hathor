using System.Text.Json;
using System.Text.RegularExpressions;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Enrichment;

// iTunes Search API + trending RSS (desktop Download.search_itunes[_multi]
// with query-cleaning fallbacks; 600x600 artwork).
public sealed class ITunesClientImpl(IHttpClientFactory httpFactory, ILogger<ITunesClientImpl> log) : IITunesClient
{
    private static readonly Regex Parens = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex VideoJunk = new(
        @"(?i)(official music video|official video|official lyric video|official audio|lyric video|lyrics|audio|visualizer)",
        RegexOptions.Compiled);

    public async Task<ITunesHitDto?> SearchSingleAsync(
        string title, string? artist, CancellationToken ct = default)
    {
        foreach (var query in BuildQueries(title, artist))
        {
            var hits = await SearchAsync(query, 1, ct);
            if (hits.Count > 0) return hits[0];
        }
        return null;
    }

    public Task<IReadOnlyList<ITunesHitDto>> SearchMultiAsync(
        string title, string? artist, int limit, CancellationToken ct = default)
    {
        var clean = CleanTitle(title);
        var query = string.IsNullOrWhiteSpace(artist) ? clean : $"{clean} {artist}";
        return SearchAsync(query, limit, ct);
    }

    public async Task<byte[]?> FetchArtworkAsync(string artworkUrl, CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient("itunes");
            using var res = await http.GetAsync(artworkUrl, ct);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "iTunes artwork fetch failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> GetTrendingAsync(int limit, CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient("itunes");
            using var res = await http.GetAsync(
                "https://itunes.apple.com/us/rss/topsongs/limit=10/json", ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var out_ = new List<string>();
            if (doc.RootElement.TryGetProperty("feed", out var feed) &&
                feed.TryGetProperty("entry", out var entries))
            {
                foreach (var e in entries.EnumerateArray())
                {
                    var t = e.TryGetProperty("im:name", out var n) &&
                        n.TryGetProperty("label", out var nl) ? nl.GetString() : null;
                    var a = e.TryGetProperty("im:artist", out var ar) &&
                        ar.TryGetProperty("label", out var al) ? al.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(t))
                        out_.Add(string.IsNullOrWhiteSpace(a) ? t! : $"{t} {a}");
                    if (out_.Count >= limit) break;
                }
            }
            return out_;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "iTunes trending fetch failed");
            return [];
        }
    }

    internal static string CleanTitle(string title)
    {
        var clean = title;
        if (clean.Contains(" - ")) clean = clean.Split([" - "], 2, StringSplitOptions.None)[^1];
        clean = Parens.Replace(clean, "");
        clean = Brackets.Replace(clean, "");
        clean = VideoJunk.Replace(clean, "");
        return string.Join(' ', clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    internal static List<string> BuildQueries(string title, string? artist)
    {
        var clean = CleanTitle(title);
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
        {
            queries.Add($"{clean} {artist}");
            queries.Add($"{title} {artist}");
        }
        queries.Add(clean);
        queries.Add(title);
        return queries.Where(q => !string.IsNullOrWhiteSpace(q)).Distinct().ToList();
    }

    private async Task<IReadOnlyList<ITunesHitDto>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("itunes");
            var url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query) +
                $"&entity=song&limit={limit}";
            using var res = await http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var out_ = new List<ITunesHitDto>();
            if (!doc.RootElement.TryGetProperty("results", out var results)) return out_;
            foreach (var r in results.EnumerateArray())
            {
                string? Str(string k) =>
                    r.TryGetProperty(k, out var v) ? v.GetString() : null;
                var art100 = Str("artworkUrl100") ?? "";
                out_.Add(new ITunesHitDto(
                    Str("trackName") ?? query,
                    Str("artistName") ?? "",
                    Str("collectionName") ?? "",
                    (Str("releaseDate") ?? "").Length >= 4 ? Str("releaseDate")![..4] : "",
                    Str("primaryGenreName") ?? "",
                    art100.Replace("100x100bb", "600x600bb")));
            }
            return out_;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "iTunes search failed for {Query}", query);
            return [];
        }
    }
}
