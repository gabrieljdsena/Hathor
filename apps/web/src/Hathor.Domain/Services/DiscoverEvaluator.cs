namespace Hathor.Domain.Services;

// Stored daily ranking snapshot (Rec_Impressions.ItemsJson). Features are the
// exact values the ranker used, so offline presets can be compared without
// re-hitting MusicBrainz/ListenBrainz.
public sealed record DiscoverImpressionItem(
    string Title,
    string Artist,
    string? Mbid,
    string Source,
    string? SeedArtist,
    IReadOnlyList<string>? Tags,
    double Score,
    DiscoverItemFeatures Features);

// One ranked item joined with the feedback event attributed to that
// snapshot (null = no observed outcome; not treated as a negative).
public sealed record DiscoverEvaluationItem(
    string SnapshotDate,
    int Rank,
    string ItemKey,
    string Source,
    string Artist,
    IReadOnlyList<string>? Tags,
    DiscoverItemFeatures Features,
    string? Event);

public sealed record DiscoverSourceEvaluation(
    string Source,
    int Labels,
    int Downloads,
    int Dismissals,
    double ConversionRate);

public sealed record DiscoverPresetEvaluation(
    string Name,
    double PrecisionAtK,
    double NdcgAtK,
    double ConversionAtK);

public sealed record DiscoverEvaluationReport(
    int WindowDays,
    int K,
    int Snapshots,
    int Items,
    int Labeled,
    int Downloads,
    int Dismissals,
    int Previews,
    int Clicks,
    double PrecisionAtK,
    double NdcgAtK,
    double ConversionAtK,
    double ArtistCoverage,
    bool InsufficientData,
    IReadOnlyList<DiscoverSourceEvaluation> Sources,
    IReadOnlyList<DiscoverPresetEvaluation> Presets);

public sealed record DiscoverEvaluationPreset(
    string Name,
    double SimilarityWeight,
    double TagWeight,
    double AffinityWeight,
    double PopularityWeight,
    double SourceWeight,
    double AudioWeight = 0.10)
{
    public DiscoverRankingWeights ToWeights() => new(
        SimilarityWeight, TagWeight, AffinityWeight,
        PopularityWeight, SourceWeight, AudioWeight);
}

public sealed record DiscoverRankingWeights(
    double Similarity,
    double Tag,
    double Affinity,
    double Popularity,
    double Source,
    double Audio);

public sealed record DiscoverTuningResult(
    bool InsufficientData,
    bool Applied,
    string? Preset,
    int Snapshots,
    int Labeled,
    double CurrentScore,
    double BestScore,
    DiscoverRankingWeights? Weights);

public static class DiscoverEvaluator
{
    public const int DefaultK = 10;
    public const int DefaultMinSnapshots = 3;
    public const int DefaultMinLabels = 10;

    public static readonly IReadOnlyList<DiscoverEvaluationPreset> Presets =
    [
        new("similarity-heavy", 0.55, 0.15, 0.10, 0.10, 0.10),
        new("tag-heavy", 0.25, 0.45, 0.10, 0.10, 0.10),
        new("affinity-heavy", 0.20, 0.15, 0.45, 0.10, 0.10),
        new("popularity-heavy", 0.20, 0.15, 0.10, 0.45, 0.10),
        new("history-heavy", 0.20, 0.25, 0.20, 0.10, 0.10, 0.15),
        new("audio-heavy", 0.20, 0.15, 0.10, 0.10, 0.10, 0.35),
        new("balanced", 0.25, 0.25, 0.20, 0.15, 0.05, 0.10),
    ];

    public static DiscoverEvaluationReport Evaluate(
        IReadOnlyList<DiscoverEvaluationItem> items,
        int windowDays,
        int k,
        int snapshotCount,
        int minSnapshots = DefaultMinSnapshots,
        int minLabels = DefaultMinLabels)
    {
        k = Math.Clamp(k, 1, 50);
        var labeled = items.Where(i => i.Event is not null).ToList();
        var downloads = labeled.Count(i => i.Event == DiscoverFeedbackEvents.Download);
        var dismissals = labeled.Count(i => i.Event == DiscoverFeedbackEvents.Dismiss);
        var previews = labeled.Count(i => i.Event == DiscoverFeedbackEvents.Preview);
        var clicks = labeled.Count(i => i.Event == DiscoverFeedbackEvents.Click);

        var (precision, ndcg, conversion) = Metrics(items, k);
        var coverage = items.Count == 0
            ? 0
            : items.Select(i => MediaKeys.Normalize(i.Artist))
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Count() / (double)items.Count;

        var sources = labeled
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Source) ? "unknown" : i.Source,
                StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var d = g.Count(i => i.Event == DiscoverFeedbackEvents.Download);
                var x = g.Count(i => i.Event == DiscoverFeedbackEvents.Dismiss);
                return new DiscoverSourceEvaluation(
                    g.Key, g.Count(), d, x,
                    d + x == 0 ? 0.5 : d / (double)(d + x));
            })
            .OrderByDescending(s => s.Labels)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var presets = new List<DiscoverPresetEvaluation>
        {
            new("current", precision, ndcg, conversion),
        };
        foreach (var preset in Presets)
        {
            var reRanked = ReRank(items, preset);
            var m = Metrics(reRanked, k);
            presets.Add(new DiscoverPresetEvaluation(
                preset.Name, m.Precision, m.Ndcg, m.Conversion));
        }

        return new DiscoverEvaluationReport(
            windowDays,
            k,
            snapshotCount,
            items.Count,
            labeled.Count,
            downloads,
            dismissals,
            previews,
            clicks,
            precision,
            ndcg,
            conversion,
            coverage,
            snapshotCount < minSnapshots || labeled.Count < minLabels,
            sources,
            presets);
    }

    private static (double Precision, double Ndcg, double Conversion) Metrics(
        IReadOnlyList<DiscoverEvaluationItem> items, int k)
    {
        // Days with no observed outcome are not evidence of bad ranking;
        // average precision/nDCG over snapshots that actually have labels.
        var groups = items
            .GroupBy(i => i.SnapshotDate)
            .Where(g => g.Any(i => i.Event is not null))
            .ToList();
        if (groups.Count == 0) return (0, 0, 0.5);

        var precisionSum = 0.0;
        var ndcgSum = 0.0;
        var ndcgGroups = 0;
        var downloads = 0;
        var dismissals = 0;

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(i => i.Rank).Take(k).ToList();
            var denominator = Math.Max(1, Math.Min(k, group.Count()));
            var groupDownloads = ordered.Count(i => i.Event == DiscoverFeedbackEvents.Download);
            downloads += groupDownloads;
            dismissals += ordered.Count(i => i.Event == DiscoverFeedbackEvents.Dismiss);
            precisionSum += groupDownloads / (double)denominator;

            var dcg = 0.0;
            for (var i = 0; i < ordered.Count; i++)
                dcg += Gain(ordered[i].Event) / Math.Log2(i + 2);

            var ideal = group
                .Select(i => Gain(i.Event))
                .Where(g => g > 0)
                .OrderByDescending(g => g)
                .Take(k)
                .ToList();
            if (ideal.Count == 0) continue;

            var idcg = 0.0;
            for (var i = 0; i < ideal.Count; i++)
                idcg += ideal[i] / Math.Log2(i + 2);
            if (idcg <= 0) continue;

            ndcgSum += dcg / idcg;
            ndcgGroups++;
        }

        return (
            precisionSum / groups.Count,
            ndcgGroups == 0 ? 0 : ndcgSum / ndcgGroups,
            downloads + dismissals == 0
                ? 0.5
                : downloads / (double)(downloads + dismissals));
    }

    private static List<DiscoverEvaluationItem> ReRank(
        IReadOnlyList<DiscoverEvaluationItem> items,
        DiscoverEvaluationPreset preset)
    {
        var total = preset.SimilarityWeight + preset.TagWeight +
            preset.AffinityWeight + preset.PopularityWeight + preset.SourceWeight +
            preset.AudioWeight;
        if (total <= 0) total = 1;

        var output = new List<DiscoverEvaluationItem>();
        foreach (var group in items.GroupBy(i => i.SnapshotDate))
        {
            var ordered = group
                .Select(item => (Item: item, Score: PresetScore(item.Features, preset, total)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Rank)
                .ToList();

            var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
            var selected = new List<DiscoverEvaluationItem>();
            var skipped = new List<DiscoverEvaluationItem>();
            foreach (var (item, _) in ordered)
            {
                var artist = MediaKeys.Normalize(item.Artist);
                if (artist.Length > 0)
                {
                    perArtist.TryGetValue(artist, out var used);
                    if (used >= 2)
                    {
                        skipped.Add(item);
                        continue;
                    }
                    perArtist[artist] = used + 1;
                }
                selected.Add(item);
            }

            var rank = 0;
            foreach (var item in selected.Concat(skipped))
            {
                rank++;
                output.Add(item with { Rank = rank });
            }
        }
        return output;
    }

    private static double PresetScore(
        DiscoverItemFeatures features, DiscoverEvaluationPreset preset, double total)
    {
        // Old snapshots (pre-CLAP) have no audio value; treat as neutral.
        var audio = features.AudioSimilarity > 0 ? features.AudioSimilarity : 0.5;
        return Math.Clamp(
            (preset.SimilarityWeight * features.Similarity +
             preset.TagWeight * features.TagScore +
             preset.AffinityWeight * features.Affinity +
             preset.PopularityWeight * features.Popularity +
             preset.SourceWeight * features.SourcePrior +
             preset.AudioWeight * audio) / total +
            features.LikedSeedBoost + features.LikedArtistBoost +
            features.EngagedArtistBoost + features.LearnedArtistBoost +
            features.LearnedSeedBoost,
            0, 1);
    }

    // Picks the best preset by a blended rank-aware score. It only reports
    // Applied when the improvement clears a margin and there is enough data;
    // the caller decides whether to persist it.
    public static DiscoverTuningResult Tune(
        IReadOnlyList<DiscoverEvaluationItem> items,
        int windowDays,
        int k,
        int snapshotCount,
        int minSnapshots = DefaultMinSnapshots,
        int minLabels = DefaultMinLabels,
        double margin = 0.02)
    {
        var report = Evaluate(items, windowDays, k, snapshotCount,
            minSnapshots, minLabels);
        if (report.InsufficientData)
            return new DiscoverTuningResult(
                true, false, null, report.Snapshots, report.Labeled, 0, 0, null);

        var current = report.Presets.First(p => p.Name == "current");
        var currentScore = Composite(current);
        var best = report.Presets
            .Where(p => p.Name != "current")
            .OrderByDescending(Composite)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null)
            return new DiscoverTuningResult(
                false, false, null, report.Snapshots, report.Labeled,
                currentScore, currentScore, null);

        var bestScore = Composite(best);
        var applied = bestScore >= currentScore + Math.Max(0, margin);
        var preset = Presets.FirstOrDefault(p => p.Name == best.Name);
        return new DiscoverTuningResult(
            false,
            applied,
            best.Name,
            report.Snapshots,
            report.Labeled,
            currentScore,
            bestScore,
            preset?.ToWeights());
    }

    private static double Composite(DiscoverPresetEvaluation preset) =>
        0.6 * preset.NdcgAtK + 0.3 * preset.PrecisionAtK +
        0.1 * preset.ConversionAtK;

    private static double Gain(string? @event) => @event switch
    {
        DiscoverFeedbackEvents.Download => 2,
        DiscoverFeedbackEvents.Preview => 1,
        DiscoverFeedbackEvents.Click => 1,
        _ => 0,
    };
}
