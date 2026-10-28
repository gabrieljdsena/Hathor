namespace Hathor.Domain.Services;

// Genre/tag affinity for Discover. Local tags are broad ("Rock"), while
// MusicBrainz returns tokens like "alternative rock" or "shoegaze"; exact
// equality misses most of those. This matcher combines containment,
// shared-token overlap and a small genre-family map.
public static class TagMatcher
{
    private static readonly char[] Separators = ['/', ';', ',', '|'];

    private static readonly Dictionary<string, string[]> FamilyMap = BuildFamilies();

    public static double Similarity(string? a, string? b)
    {
        var aVariants = Variants(a);
        var bVariants = Variants(b);
        if (aVariants.Count == 0 || bVariants.Count == 0) return 0;

        var best = 0.0;
        foreach (var left in aVariants)
            foreach (var right in bVariants)
                best = Math.Max(best, Score(left, right));
        return best;
    }

    public static IReadOnlyList<string> Variants(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return [];
        var parts = tag.Split(Separators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) parts = [tag];

        var variants = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            var normalized = NormalizeFragment(part);
            if (normalized.Length > 0 && seen.Add(normalized))
                variants.Add(normalized);
        }
        return variants;
    }

    private static string NormalizeFragment(string value)
    {
        // Keep hyphen/ampersand boundaries as spaces: "Synth-Pop" and
        // "R&B" become token pairs instead of one glued word.
        var cleaned = value
            .Replace('-', ' ')
            .Replace('&', ' ')
            .Replace('.', ' ');
        return MediaKeys.Normalize(cleaned);
    }

    public static IReadOnlySet<string> Families(string? tag)
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in Variants(tag))
            if (FamilyMap.TryGetValue(variant, out var mapped))
                families.UnionWith(mapped);
        return families;
    }

    private static double Score(string left, string right)
    {
        if (left == right) return 1.0;

        var leftTokens = Tokens(left);
        var rightTokens = Tokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return 0;

        var subset = leftTokens.IsSubsetOf(rightTokens) ||
            rightTokens.IsSubsetOf(leftTokens);
        if (subset) return 0.85;

        var shared = leftTokens.Count(t => rightTokens.Contains(t));
        var leftFamily = FamilyMap.TryGetValue(left, out var lf) ? lf : [];
        var rightFamily = FamilyMap.TryGetValue(right, out var rf) ? rf : [];
        var sameFamily = leftFamily.Intersect(rightFamily, StringComparer.Ordinal).Any();

        if (sameFamily) return shared > 0 ? 0.70 : 0.65;
        return shared > 0 ? 0.40 : 0;
    }

    private static HashSet<string> Tokens(string value) =>
        new(value.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static Dictionary<string, string[]> BuildFamilies()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);

        void Add(string[] families, params string[] tags)
        {
            foreach (var tag in tags)
            {
                var key = NormalizeFragment(tag);
                if (key.Length == 0) continue;
                map[key] = map.TryGetValue(key, out var existing)
                    ? existing.Union(families, StringComparer.Ordinal).ToArray()
                    : families;
            }
        }

        Add(["rock"], "rock", "alternative rock", "alt rock", "indie rock",
            "hard rock", "soft rock", "garage rock", "psychedelic rock",
            "progressive rock", "art rock", "post-rock", "math rock", "grunge",
            "shoegaze", "emo", "screamo", "britpop", "rockabilly", "surf rock");
        Add(["rock", "pop"], "pop rock");
        Add(["rock", "punk"], "punk rock", "pop punk");
        Add(["rock", "folk"], "folk rock");
        Add(["rock", "blues"], "blues rock");
        Add(["rock", "country"], "country rock");
        Add(["rock", "metal"], "nu metal", "metalcore", "industrial rock");
        Add(["pop"], "pop", "synth pop", "synthpop", "dream pop", "indie pop",
            "electropop", "art pop", "chamber pop", "k pop", "j pop", "city pop",
            "dance pop", "power pop");
        Add(["electronic"], "electronic", "electronica", "edm", "house",
            "deep house", "tech house", "techno", "trance", "ambient", "idm",
            "downtempo", "trip hop", "drum and bass", "drum bass", "dnb",
            "dubstep", "breakbeat", "electro", "synthwave", "chiptune",
            "vaporwave", "future bass", "garage", "uk garage");
        Add(["hip hop"], "hip hop", "rap", "trap", "boom bap", "grime", "drill",
            "g funk", "conscious hip hop");
        Add(["jazz"], "jazz", "bebop", "jazz fusion", "smooth jazz", "free jazz",
            "swing", "bossa nova");
        Add(["classical"], "classical", "baroque", "romantic", "opera",
            "orchestral", "contemporary classical", "choral");
        Add(["metal"], "metal", "heavy metal", "death metal", "black metal",
            "thrash metal", "doom metal", "power metal", "symphonic metal",
            "sludge metal");
        Add(["punk"], "punk", "hardcore punk", "post punk", "punk rock");
        Add(["folk"], "folk", "americana", "bluegrass", "singer songwriter",
            "folk rock", "celtic");
        Add(["country"], "country", "country rock", "outlaw country", "alt country");
        Add(["blues"], "blues", "delta blues", "electric blues", "blues rock");
        Add(["soul"], "soul", "funk", "r b", "rb", "neo soul", "motown", "gospel",
            "disco", "nu disco", "italo disco");
        Add(["reggae"], "reggae", "dub", "ska", "dancehall", "rocksteady");
        Add(["latin"], "latin", "salsa", "bachata", "merengue", "cumbia",
            "reggaeton", "mpb", "samba", "tango", "bossa nova");
        Add(["world"], "world", "afrobeat", "highlife", "flamenco", "bollywood",
            "kirtan", "celtic");
        Add(["soundtrack"], "soundtrack", "score", "film score",
            "video game music", "musical");
        Add(["experimental"], "experimental", "avant garde", "noise",
            "industrial", "industrial rock");
        Add(["indie"], "indie");
        return map;
    }
}
