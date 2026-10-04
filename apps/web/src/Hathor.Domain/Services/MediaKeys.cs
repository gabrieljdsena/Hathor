using System.Text.RegularExpressions;

namespace Hathor.Domain.Services;

// Normalized title+artist identity shared by library dedupe, download
// ownership checks and Discover ranking: one normalization everywhere so
// "Song (Remastered)", "Song - Single" and "Song feat. X" converge to one
// key no matter which side names the variant.
public static partial class MediaKeys
{
    public static string Key(string title, string artist) =>
        Normalize(title) + "\u0001" + Normalize(artist);

    public static bool IsSameArtist(string a, string b) =>
        Normalize(a) == Normalize(b);

    public static string Normalize(string value)
    {
        var s = (value ?? "").ToLowerInvariant();
        s = ParensRegex().Replace(s, "");
        s = BracketsRegex().Replace(s, "");
        s = FeaturingRegex().Replace(s, "");
        while (StripEditionSuffix(ref s)) { }
        s = JunkCharsRegex().Replace(s, "");
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly string[] EditionPrefixes =
    [
        "single", "ep", "remaster", "remastered", "deluxe", "live", "acoustic", "demo",
        "radio edit", "reprise", "reissue", "expanded", "bonus", "mono",
        "stereo", "topic", "explicit",
    ];

    private static bool StripEditionSuffix(ref string s)
    {
        var idx = s.LastIndexOf(" - ", StringComparison.Ordinal);
        if (idx < 0) return false;
        var tail = s[(idx + 3)..].Trim();
        foreach (var prefix in EditionPrefixes)
        {
            if (tail.Equals(prefix, StringComparison.Ordinal) ||
                tail.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                s = s[..idx].TrimEnd();
                return true;
            }
        }
        return false;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParensRegex();
    [GeneratedRegex(@"\[([^\]]*)\]")]
    private static partial Regex BracketsRegex();
    // Bare feat tails outside parens ("Song feat. X", "Song featuring Y").
    // Requires the dot or the full word — "50 Ft Queenie" must survive.
    // (No trailing \b: after "feat." comes a space, which is no boundary.)
    [GeneratedRegex(@"\b(feat\.|ft\.|featuring).*$")]
    private static partial Regex FeaturingRegex();
    // Punctuation that splits variants ("Don't" vs "Dont", "R&B" vs "RB").
    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex JunkCharsRegex();
}
