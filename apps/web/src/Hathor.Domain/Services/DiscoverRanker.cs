namespace Hathor.Domain.Services;

// Out-of-library ranking (packages/contracts/discover.md): pure, no I/O.
// Candidates come from open-data similarity sources (ListenBrainz /
// MusicBrainz), local artist expansion and a chart fallback for cold start.
// Everything matching the owned library or an in-flight download is dropped,
// then candidates are scored with our own feature blend + MMR diversity.
public sealed record DiscoverCandidate(
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string ArtworkUrl,
    string Source,
    string? Mbid = null,
    string? SeedArtist = null,
    double Similarity = 0,
    long Popularity = 0,
    IReadOnlyList<string>? Tags = null,
    double? AudioSimilarity = null);

public sealed record RankedDiscoverItem(
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string ArtworkUrl,
    string Source,
    double Score,
    string? Mbid = null,
    string? Reason = null,
    string? SeedArtist = null,
    IReadOnlyList<string>? Tags = null,
    DiscoverItemFeatures? Features = null);

// Per-item feature snapshot used by offline evaluation/re-ranking. Scores
// are 0..1; boosts can be negative. BaseScore is before MMR; FinalScore is
// the exposed selected score.
public sealed record DiscoverItemFeatures(
    double Similarity,
    double TagScore,
    double Affinity,
    double Popularity,
    double SourcePrior,
    double LikedSeedBoost,
    double LikedArtistBoost,
    double EngagedArtistBoost,
    double LearnedArtistBoost,
    double LearnedSeedBoost,
    double BaseScore,
    double MmrPenalty,
    double FinalScore,
    double AudioSimilarity = 0);

public static class DiscoverSources
{
    // Legacy sources kept for cached rows / older clients.
    public const string Artist = "artist";
    public const string Chart = "chart";
    public const string Llm = "llm";

    // Open-data / self-owned sources.
    public const string Similar = "similar";
    public const string Cf = "cf";
    public const string Graph = "graph";
    public const string Tag = "tag";
    public const string Audio = "audio";
    public const string Popular = "popular";
}

public sealed record DiscoverRankingOptions
{
    public double SimilarityWeight { get; init; } = 0.35;
    public double TagWeight { get; init; } = 0.25;
    public double AffinityWeight { get; init; } = 0.15;
    public double PopularityWeight { get; init; } = 0.15;
    public double SourceWeight { get; init; } = 0.10;
    public double AudioWeight { get; init; } = 0.10;
    public double MmrLambda { get; init; } = 0.25;
    public int MaxPerArtist { get; init; } = 2;
    public int MaxItems { get; init; } = 50;
    public long PopularityMin { get; init; } = 10_000;
    public long PopularityMax { get; init; } = 2_000_000;
    // Below this count is treated as obscure (0 disables the penalty).
    public long PopularityFloor { get; init; } = 1_000;

    // Implicit feedback boosts (see DiscoverFeedbackSignals).
    public double LikedArtistBoost { get; init; } = 0.08;
    public double LikedSeedBoost { get; init; } = 0.08;
    public double EngagedArtistBoost { get; init; } = 0.04;

    public static readonly DiscoverRankingOptions Default = new();
}

public static partial class DiscoverRanker
{
    public const int MaxPerArtist = 2;
    public const int MaxItems = 50;

    // Legacy tuple shape (older callers/tests): all-time plays become weights.
    public static IReadOnlyList<RankedDiscoverItem> Rank(
        IReadOnlyList<(string Artist, long Plays)> taste,
        IReadOnlyList<DiscoverCandidate> candidates,
        IReadOnlyCollection<(string Title, string Artist)> library,
        IReadOnlyCollection<(string Title, string Artist)> inFlight) =>
        Rank(LegacyTaste(taste), candidates, library, inFlight);

    public static IReadOnlyList<RankedDiscoverItem> Rank(
        TasteProfile taste,
        IReadOnlyList<DiscoverCandidate> candidates,
        IReadOnlyCollection<(string Title, string Artist)> library,
        IReadOnlyCollection<(string Title, string Artist)> inFlight,
        IReadOnlyCollection<(string Title, string Artist)>? dismissed = null,
        DiscoverFeedbackSignals? feedback = null,
        PopularityProfile? popularity = null,
        DiscoverRankingOptions? options = null)
    {
        options ??= DiscoverRankingOptions.Default;

        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (t, a) in library) owned.Add(Key(t, a));
        foreach (var (t, a) in inFlight) owned.Add(Key(t, a));

        var dismissedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (dismissed is not null)
            foreach (var (t, a) in dismissed) dismissedKeys.Add(Key(t, a));
        if (feedback is not null) dismissedKeys.UnionWith(feedback.DismissedKeys);

        var dismissedArtists = new HashSet<string>(StringComparer.Ordinal);
        if (feedback is not null) dismissedArtists.UnionWith(feedback.DismissedArtists);

        var emitted = new HashSet<string>(StringComparer.Ordinal);

        var artistWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var a in taste.Artists)
            artistWeights[MediaKeys.Normalize(a.Artist)] = Math.Max(0, a.Weight);
        var maxArtistWeight = artistWeights.Count > 0 ? artistWeights.Values.Max() : 0;
        if (maxArtistWeight <= 0) maxArtistWeight = 1;

        var tasteTags = taste.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalWeight = options.SimilarityWeight + options.TagWeight +
            options.AffinityWeight + options.PopularityWeight + options.SourceWeight +
            options.AudioWeight;
        if (totalWeight <= 0) totalWeight = 1;

        var scored = new List<ScoredCandidate>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Artist))
                continue;
            var key = Key(c.Title, c.Artist);
            if (owned.Contains(key) || dismissedKeys.Contains(key))
                continue;
            // Same song from two sources (similar + graph + tag): first
            // occurrence wins, the rest are repeats.
            if (!emitted.Add(key))
                continue;

            var artistKey = MediaKeys.Normalize(c.Artist);
            if (dismissedArtists.Contains(artistKey))
                continue;

            var affinity = artistWeights.TryGetValue(artistKey, out var w) && maxArtistWeight > 0
                ? Math.Clamp(w / maxArtistWeight, 0, 1)
                : 0;
            var tagScore = TagScore(tasteTags, c.Tags);
            var popularityScore = PopularityScore(c.Popularity, options, popularity);
            var similarity = Math.Clamp(c.Similarity, 0, 1);
            // Unknown audio (no CLAP embedding yet) is neutral, not zero.
            var audio = c.AudioSimilarity is { } a ? Math.Clamp(a, 0, 1) : 0.5;
            var source = SourcePrior(c.Source);
            if (feedback is not null &&
                feedback.SourcePriors.TryGetValue(c.Source, out var learnedPrior))
                source = Math.Clamp(0.5 * source + 0.5 * learnedPrior, 0, 1);

            var score = (options.SimilarityWeight * similarity +
                options.TagWeight * tagScore +
                options.AffinityWeight * affinity +
                options.PopularityWeight * popularityScore +
                options.SourceWeight * source +
                options.AudioWeight * audio) / totalWeight;

            var likedSeedBoost = 0.0;
            var likedArtistBoost = 0.0;
            var engagedArtistBoost = 0.0;
            var learnedArtistBoost = 0.0;
            var learnedSeedBoost = 0.0;
            if (feedback is not null)
            {
                var seedKey = string.IsNullOrWhiteSpace(c.SeedArtist)
                    ? ""
                    : MediaKeys.Normalize(c.SeedArtist);
                if (seedKey.Length > 0 && feedback.LikedSeeds.Contains(seedKey))
                    likedSeedBoost = options.LikedSeedBoost;
                if (feedback.LikedArtists.Contains(artistKey))
                    likedArtistBoost = options.LikedArtistBoost;
                else if (feedback.EngagedArtists.Contains(artistKey))
                    engagedArtistBoost = options.EngagedArtistBoost;

                // Learned empirical boosts (downloads vs dismissals per
                // artist/seed). Smoothed in the service; capped here.
                if (feedback.ArtistBoosts.TryGetValue(artistKey, out var artistBoost))
                    learnedArtistBoost = artistBoost;
                if (seedKey.Length > 0 &&
                    feedback.SeedBoosts.TryGetValue(seedKey, out var seedBoost))
                    learnedSeedBoost = seedBoost;
            }

            var baseScore = Math.Clamp(
                score + likedSeedBoost + likedArtistBoost + engagedArtistBoost +
                learnedArtistBoost + learnedSeedBoost, 0, 1);
            var features = new DiscoverItemFeatures(
                similarity, tagScore, affinity, popularityScore, source,
                likedSeedBoost, likedArtistBoost, engagedArtistBoost,
                learnedArtistBoost, learnedSeedBoost, baseScore, 0, baseScore, audio);

            scored.Add(new ScoredCandidate(
                c, key, artistKey, baseScore, ReasonFor(c), features));
        }

        if (scored.Count == 0) return [];

        return SelectDiverse(scored, options);
    }

    public static bool IsSameArtist(string candidateArtist, string tasteArtist) =>
        MediaKeys.IsSameArtist(candidateArtist, tasteArtist);

    private static List<RankedDiscoverItem> SelectDiverse(
        List<ScoredCandidate> scored, DiscoverRankingOptions options)
    {
        var maxItems = Math.Max(0, options.MaxItems);
        var maxPerArtist = Math.Max(1, options.MaxPerArtist);
        var selected = new List<(ScoredCandidate Candidate, double Score, double Penalty)>();
        var remaining = new List<ScoredCandidate>(scored);
        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);

        while (selected.Count < maxItems && remaining.Count > 0)
        {
            ScoredCandidate? best = null;
            var bestScore = double.NegativeInfinity;
            var bestPenalty = 0.0;

            foreach (var candidate in remaining)
            {
                perArtist.TryGetValue(candidate.ArtistKey, out var used);
                if (used >= maxPerArtist) continue;

                var penalty = selected.Count == 0
                    ? 0
                    : selected.Max(s => ItemSimilarity(candidate.Candidate, s.Candidate.Candidate))
                        * options.MmrLambda;
                var adjusted = Math.Clamp(candidate.BaseScore - penalty, 0, 1);
                if (adjusted > bestScore)
                {
                    bestScore = adjusted;
                    bestPenalty = penalty;
                    best = candidate;
                }
            }

            if (best is null) break;

            remaining.Remove(best);
            selected.Add((best, bestScore, bestPenalty));
            perArtist[best.ArtistKey] = perArtist.GetValueOrDefault(best.ArtistKey) + 1;
        }

        return selected
            .Select(s => new RankedDiscoverItem(
                s.Candidate.Candidate.Title,
                s.Candidate.Candidate.Artist,
                s.Candidate.Candidate.Album,
                s.Candidate.Candidate.Year,
                s.Candidate.Candidate.Genre,
                s.Candidate.Candidate.ArtworkUrl,
                s.Candidate.Candidate.Source,
                Math.Round(s.Score, 4),
                s.Candidate.Candidate.Mbid,
                s.Candidate.Reason,
                s.Candidate.Candidate.SeedArtist,
                s.Candidate.Candidate.Tags,
                s.Candidate.Features with
                {
                    MmrPenalty = Math.Round(s.Penalty, 4),
                    FinalScore = Math.Round(s.Score, 4),
                }))
            .ToList();
    }

    private static double TagScore(
        IReadOnlyList<string> tasteTags, IReadOnlyList<string>? candidateTags)
    {
        if (tasteTags.Count == 0) return 0.5;
        if (candidateTags is null || candidateTags.Count == 0) return 0.5;

        // Fuzzy genre affinity: each historical tag takes its best match
        // against the candidate tags (containment/family/token overlap).
        var matched = 0.0;
        foreach (var tasteTag in tasteTags)
        {
            var best = 0.0;
            foreach (var candidateTag in candidateTags)
                best = Math.Max(best, TagMatcher.Similarity(tasteTag, candidateTag));
            matched += best;
        }
        return Math.Clamp(matched / tasteTags.Count, 0, 1);
    }

    private static double PopularityScore(
        long popularity, DiscoverRankingOptions options, PopularityProfile? profile)
    {
        // Unknown popularity is neutral: obscure tracks must not be punished.
        if (popularity <= 0) return 0.5;
        if (options.PopularityFloor > 0 && popularity < options.PopularityFloor)
            return 0.1;

        // Personalized band: stay close to the user's historical median
        // (log10 listen count) instead of a fixed mainstream target.
        if (profile is not null)
        {
            var delta = Math.Abs(Math.Log10(popularity) - profile.MedianLog);
            return delta <= 0.5
                ? 1.0
                : Math.Clamp(1.0 - (delta - 0.5) / 1.5, 0.1, 1.0);
        }

        var min = Math.Max(1, options.PopularityMin);
        var max = Math.Max(min, options.PopularityMax);
        if (popularity >= min && popularity <= max) return 1.0;

        var log = Math.Log10(popularity);
        if (popularity < min)
        {
            var floor = Math.Max(1, Math.Log10(min + 1));
            return Math.Clamp(0.4 + 0.6 * (log / floor), 0.2, 0.99);
        }

        var ceiling = Math.Max(1, Math.Log10(max + 1));
        var over = (log - ceiling) / ceiling;
        return Math.Clamp(1.0 - 0.8 * over, 0.2, 0.99);
    }

    private static double SourcePrior(string source) => source switch
    {
        DiscoverSources.Similar => 1.0,
        DiscoverSources.Cf => 1.0,
        DiscoverSources.Audio => 1.0,
        DiscoverSources.Graph => 0.85,
        DiscoverSources.Artist => 0.75,
        DiscoverSources.Tag => 0.70,
        DiscoverSources.Llm => 0.70,
        DiscoverSources.Popular => 0.40,
        DiscoverSources.Chart => 0.20,
        _ => 0.50,
    };

    private static double ItemSimilarity(DiscoverCandidate a, DiscoverCandidate b)
    {
        if (MediaKeys.IsSameArtist(a.Artist, b.Artist)) return 1.0;

        var tagSimilarity = 0.0;
        var aTags = a.Tags;
        var bTags = b.Tags;
        if (aTags is { Count: > 0 } && bTags is { Count: > 0 })
        {
            foreach (var left in aTags)
                foreach (var right in bTags)
                    tagSimilarity = Math.Max(tagSimilarity,
                        TagMatcher.Similarity(left, right));
        }

        var sameGenre = !string.IsNullOrWhiteSpace(a.Genre) &&
            MediaKeys.Normalize(a.Genre) == MediaKeys.Normalize(b.Genre);
        if (sameGenre) tagSimilarity = Math.Max(tagSimilarity, 0.4);
        return Math.Clamp(tagSimilarity, 0, 1);
    }

    private static string? ReasonFor(DiscoverCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.SeedArtist))
            return $"Because you play {candidate.SeedArtist}";
        return candidate.Source switch
        {
            DiscoverSources.Tag => "From a genre you listen to",
            DiscoverSources.Popular => "Popular right now",
            DiscoverSources.Chart => "Popular right now",
            _ => null,
        };
    }

    private static TasteProfile LegacyTaste(IReadOnlyList<(string Artist, long Plays)> taste)
    {
        if (taste.Count == 0) return TasteProfile.Empty;
        var max = Math.Max(1, taste.Max(t => t.Plays));
        var artists = taste
            .Select(t => new TasteArtist(t.Artist, t.Plays, (double)t.Plays / max))
            .ToList();
        return new TasteProfile(artists, [], []);
    }

    // Shared library identity (MediaKeys): variants converge on both sides.
    private static string Key(string title, string artist) =>
        MediaKeys.Key(title, artist);

    private sealed record ScoredCandidate(
        DiscoverCandidate Candidate,
        string Key,
        string ArtistKey,
        double BaseScore,
        string? Reason,
        DiscoverItemFeatures Features);
}
