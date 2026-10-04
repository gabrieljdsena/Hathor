using System.Text.RegularExpressions;

namespace Hathor.Domain.Services;

// Out-of-library ranking (packages/contracts/discover.md): pure, no I/O.
// Taste = top artists by history play counts (ordered desc). Candidates come
// from iTunes expansion / charts / LLM; anything matching the owned library
// or an in-flight download is dropped, then affinity-scored with a per-artist
// diversity cap.
public sealed record DiscoverCandidate(
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string ArtworkUrl,
    string Source);

public sealed record RankedDiscoverItem(
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string ArtworkUrl,
    string Source,
    double Score);

public static class DiscoverSources
{
    public const string Artist = "artist";
    public const string Chart = "chart";
    public const string Llm = "llm";
}

public static partial class DiscoverRanker
{
    public const int MaxPerArtist = 2;
    public const int MaxItems = 30;

    public static IReadOnlyList<RankedDiscoverItem> Rank(
        IReadOnlyList<(string Artist, long Plays)> taste,
        IReadOnlyList<DiscoverCandidate> candidates,
        IReadOnlyCollection<(string Title, string Artist)> library,
        IReadOnlyCollection<(string Title, string Artist)> inFlight)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (t, a) in library) owned.Add(Key(t, a));
        foreach (var (t, a) in inFlight) owned.Add(Key(t, a));

        var affinity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var maxPlays = taste.Count > 0 ? Math.Max(taste.Max(t => t.Plays), 1L) : 1L;
        foreach (var (artist, plays) in taste)
            affinity[artist] = (double)plays / maxPlays;

        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scored = new List<RankedDiscoverItem>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Artist))
                continue;
            if (owned.Contains(Key(c.Title, c.Artist)))
                continue;
            perArtist.TryGetValue(c.Artist, out var used);
            if (used >= MaxPerArtist)
                continue;
            perArtist[c.Artist] = used + 1;

            var aff = affinity.TryGetValue(c.Artist, out var w) ? w : 0.0;
            var score = 0.7 * aff + 0.3 * SourceWeight(c.Source);
            scored.Add(new RankedDiscoverItem(
                c.Title, c.Artist, c.Album, c.Year, c.Genre, c.ArtworkUrl, c.Source, score));
        }

        return scored
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)
            .ToList();
    }

    public static bool IsSameArtist(string candidateArtist, string tasteArtist) =>
        Normalize(candidateArtist) == Normalize(tasteArtist);

    private static double SourceWeight(string source) => source switch
    {
        DiscoverSources.Artist => 1.0,
        DiscoverSources.Llm => 1.0,
        _ => 0.5,
    };

    private static string Key(string title, string artist) =>
        Normalize(title) + "\u0001" + Normalize(artist);

    // Contract normalization (discover.md rule 2): lowercase, trim,
    // strip (...) / [...] segments (remaster/live/feat variants), collapse space.
    private static string Normalize(string value)
    {
        var s = (value ?? "").ToLowerInvariant();
        s = ParensRegex().Replace(s, "");
        s = BracketsRegex().Replace(s, "");
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParensRegex();
    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketsRegex();
}
