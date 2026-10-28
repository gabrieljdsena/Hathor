using Hathor.Application.Dtos;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Discover;

// Offline evaluation over stored daily ranker snapshots. Feedback is
// attributed to the latest snapshot (<= 7 days old) that actually surfaced
// the item, so delayed downloads/dismissals still count without leaking
// labels into recommendations the user never saw.
public sealed record GetDiscoverEvaluationQuery(
    Guid UserId, int? WindowDays = null, int K = DiscoverEvaluator.DefaultK)
    : IRequest<DiscoverEvaluationDto>;

public sealed class GetDiscoverEvaluationHandler(
    IDiscoverImpressionRepository impressions,
    IDiscoverFeedbackRepository feedback,
    DiscoveryOptions options)
    : IRequestHandler<GetDiscoverEvaluationQuery, DiscoverEvaluationDto>
{
    public async Task<DiscoverEvaluationDto> Handle(
        GetDiscoverEvaluationQuery q, CancellationToken ct)
    {
        var window = Math.Clamp(q.WindowDays ?? options.FeedbackDays, 1, 365);
        var k = Math.Clamp(q.K, 1, 50);
        var now = DateTime.UtcNow;
        var sinceDate = DateOnly.FromDateTime(now.AddDays(-window)).ToString("yyyy-MM-dd");

        var snapshots = (await impressions.GetSinceAsync(q.UserId, sinceDate, ct))
            .Where(s => s.ModelVersion == DiscoverService.ModelVersion)
            .ToList();
        var feedbackRows = await feedback.GetSinceAsync(
            q.UserId, now.AddDays(-window), 5000, ct);
        var items = DiscoverEvaluationData.Build(snapshots, feedbackRows);
        var report = DiscoverEvaluator.Evaluate(items, window, k, snapshots.Count);
        return Map(report);
    }

    private static DiscoverEvaluationDto Map(DiscoverEvaluationReport report) =>
        new(
            report.WindowDays,
            report.K,
            report.Snapshots,
            report.Items,
            report.Labeled,
            report.Downloads,
            report.Dismissals,
            report.Previews,
            report.Clicks,
            report.PrecisionAtK,
            report.NdcgAtK,
            report.ConversionAtK,
            report.ArtistCoverage,
            report.InsufficientData,
            report.Sources.Select(s => new DiscoverSourceEvaluationDto(
                s.Source, s.Labels, s.Downloads, s.Dismissals, s.ConversionRate)).ToList(),
            report.Presets.Select(p => new DiscoverPresetEvaluationDto(
                p.Name, p.PrecisionAtK, p.NdcgAtK, p.ConversionAtK)).ToList());
}
