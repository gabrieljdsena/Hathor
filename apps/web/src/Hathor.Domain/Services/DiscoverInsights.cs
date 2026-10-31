namespace Hathor.Domain.Services;

// Pure feedback/telemetry aggregation for the Discover insights endpoint and
// the learned source priors used by the ranker. No I/O.
public sealed record DiscoverOutcomeFact(string Source, string Event, string? Artist);

public sealed record DiscoverSourceInsight(
    string Source,
    int Downloads,
    int Dismissals,
    int Previews,
    int Clicks,
    double ConversionRate,
    double LearnedPrior);

public sealed record DiscoverInsightSummary(
    int WindowDays,
    int Downloads,
    int Dismissals,
    int Previews,
    int Clicks,
    double ConversionRate,
    int CompletedPlays,
    int SkippedPlays,
    double PlaybackCompletionRate,
    int UniqueArtists,
    IReadOnlyList<DiscoverSourceInsight> Sources);

public static class DiscoverInsightCalculator
{
    public static DiscoverInsightSummary Build(
        IReadOnlyList<DiscoverOutcomeFact> facts,
        int windowDays,
        int completedPlays,
        int skippedPlays)
    {
        var sources = facts
            .GroupBy(f => string.IsNullOrWhiteSpace(f.Source) ? "unknown" : f.Source,
                StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var downloads = g.Count(f => f.Event == DiscoverFeedbackEvents.Download);
                var dismissals = g.Count(f => f.Event == DiscoverFeedbackEvents.Dismiss);
                var previews = g.Count(f => f.Event == DiscoverFeedbackEvents.Preview);
                var clicks = g.Count(f => f.Event == DiscoverFeedbackEvents.Click);
                var outcomes = downloads + dismissals;
                return new DiscoverSourceInsight(
                    g.Key,
                    downloads,
                    dismissals,
                    previews,
                    clicks,
                    Conversion(downloads, dismissals),
                    // Smoothed Bernoulli prior (Jeffreys-ish) for ranking.
                    (downloads + 1.0) / (outcomes + 2.0));
            })
            .OrderByDescending(s => s.Downloads)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalDownloads = sources.Sum(s => s.Downloads);
        var totalDismissals = sources.Sum(s => s.Dismissals);
        var totalPreviews = sources.Sum(s => s.Previews);
        var totalClicks = sources.Sum(s => s.Clicks);
        var uniqueArtists = facts
            .Select(f => MediaKeys.Normalize(f.Artist ?? ""))
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new DiscoverInsightSummary(
            windowDays,
            totalDownloads,
            totalDismissals,
            totalPreviews,
            totalClicks,
            Conversion(totalDownloads, totalDismissals),
            completedPlays,
            skippedPlays,
            completedPlays + skippedPlays == 0
                ? 0.5
                : completedPlays / (double)(completedPlays + skippedPlays),
            uniqueArtists,
            sources);
    }

    private static double Conversion(int downloads, int dismissals) =>
        downloads + dismissals == 0
            ? 0.5
            : downloads / (double)(downloads + dismissals);
}
