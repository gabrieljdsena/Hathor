using System.Globalization;
using System.Text;

namespace Hathor.Domain.Services;

// Typo-tolerant search used by every search box (library, podcasts, mix,
// playlists): exact substring always matches, otherwise every query token
// must match a haystack token within a small Damerau-Levenshtein distance
// (catches substitutions, missing/extra letters and swapped letters).
public static class FuzzySearch
{
    public static bool IsMatch(string? haystack, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(haystack)) return false;

        var hay = Normalize(haystack);
        var needle = Normalize(query);
        if (hay.Contains(needle, StringComparison.Ordinal)) return true;

        var hayTokens = Tokenize(hay);
        if (hayTokens.Count == 0) return false;
        foreach (var token in Tokenize(needle))
        {
            if (!MatchesToken(hayTokens, token)) return false;
        }
        return true;
    }

    // Multi-field variant (title/artist/album): exact hit on the joined
    // text wins, otherwise tokens may come from different fields.
    public static bool IsMatch(IEnumerable<string?> fields, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var parts = fields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .ToList();
        if (parts.Count == 0) return false;

        var joined = Normalize(string.Join(" ", parts));
        var needle = Normalize(query);
        if (joined.Contains(needle, StringComparison.Ordinal)) return true;

        var hayTokens = parts.SelectMany(p => Tokenize(Normalize(p))).ToList();
        if (hayTokens.Count == 0) return false;
        return Tokenize(needle).All(t => MatchesToken(hayTokens, t));
    }

    // Lower is better; null = no match. Exact substring scores 0.
    public static int? Score(string? haystack, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (string.IsNullOrWhiteSpace(haystack)) return null;

        var hay = Normalize(haystack);
        var needle = Normalize(query);
        if (hay.Contains(needle, StringComparison.Ordinal)) return 0;

        var hayTokens = Tokenize(hay);
        if (hayTokens.Count == 0) return null;
        var total = 0;
        foreach (var token in Tokenize(needle))
        {
            var best = BestDistance(hayTokens, token);
            if (best is null) return null;
            total += best.Value;
        }
        return total;
    }

    private static bool MatchesToken(List<string> hayTokens, string token) =>
        BestDistance(hayTokens, token) is not null;

    private static int? BestDistance(List<string> hayTokens, string token)
    {
        int? best = null;
        foreach (var hay in hayTokens)
        {
            if (hay.Contains(token, StringComparison.Ordinal)) return 0;
            var max = MaxDistance(token);
            if (Math.Abs(hay.Length - token.Length) > max) continue;
            var d = DamerauLevenshtein(hay, token, max);
            if (d <= max && (best is null || d < best)) best = d;
        }
        return best;
    }

    // Tolerance grows with token length: short tokens allow 1 edit,
    // longer ones up to 3 (capped so long queries stay selective).
    internal static int MaxDistance(string token) =>
        token.Length < 4 ? 1 : Math.Min(3, token.Length / 3);

    // Optimal string alignment (restricted Damerau-Levenshtein): adjacent
    // transpositions count as a single edit. Early-exits past maxDistance.
    internal static int DamerauLevenshtein(string a, string b, int maxDistance)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prevPrev = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var deletion = prev[j] + 1;
                var insertion = cur[j - 1] + 1;
                var substitution = prev[j - 1] + cost;
                var best = Math.Min(Math.Min(deletion, insertion), substitution);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    best = Math.Min(best, prevPrev[j - 2] + 1); // transposition
                cur[j] = best;
                if (best < rowMin) rowMin = best;
            }
            if (rowMin > maxDistance) return maxDistance + 1;
            (prevPrev, prev, cur) = (prev, cur, prevPrev);
        }
        return prev[b.Length];
    }

    internal static string Normalize(string value)
    {
        // Case/diacritic-insensitive so "Beyonce" matches "Beyoncé".
        var folded = string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
        var sb = new StringBuilder(folded.Length);
        var prevSpace = true; // collapse runs + trim via leading skip
        foreach (var c in folded.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    internal static List<string> Tokenize(string normalized) =>
        normalized.Split([' ', '-', '_', '/', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries).ToList();
}
