using System.Text.Json;
using Hathor.Application.Dtos;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Discover;

// Evaluation-driven tuning: compares weight presets on stored snapshots and
// persists a per-user model only when the improvement clears the margin and
// sample thresholds. The next Discover read sees a new cache version and
// recomputes with the tuned weights.
public sealed record TuneDiscoverCommand(Guid UserId, int? WindowDays = null, int K = 10)
    : IRequest<DiscoverTuningDto>;

public sealed record GetDiscoverTuningQuery(Guid UserId)
    : IRequest<DiscoverTuningStatusDto>;

public sealed class TuneDiscoverHandler(
    IDiscoverImpressionRepository impressions,
    IDiscoverFeedbackRepository feedback,
    IRankingModelRepository models,
    DiscoveryOptions options)
    : IRequestHandler<TuneDiscoverCommand, DiscoverTuningDto>
{
    public async Task<DiscoverTuningDto> Handle(
        TuneDiscoverCommand q, CancellationToken ct)
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
        var result = DiscoverEvaluator.Tune(
            items, window, k, snapshots.Count,
            options.TuningMinSnapshots, options.TuningMinLabels,
            options.TuningMargin);

        var applied = options.TuningEnabled && result.Applied && result.Weights is not null;
        if (applied)
        {
            var existing = await models.GetAsync(q.UserId, ct);
            await models.UpsertAsync(new RankingModel
            {
                UserId = q.UserId,
                BaseVersion = DiscoverService.ModelVersion,
                PresetName = result.Preset ?? "tuned",
                WeightsJson = JsonSerializer.Serialize(result.Weights!),
                Samples = result.Labeled,
                Snapshots = result.Snapshots,
                CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                UpdatedAtUtc = now,
            }, ct);
            await models.SaveChangesAsync(ct);
        }

        return new DiscoverTuningDto(
            applied,
            result.InsufficientData,
            result.Preset,
            result.Snapshots,
            result.Labeled,
            result.CurrentScore,
            result.BestScore,
            result.Weights is null ? null : ToDto(result.Weights));
    }

    internal static DiscoverTuningWeightsDto ToDto(DiscoverRankingWeights weights) =>
        new(weights.Similarity, weights.Tag, weights.Affinity,
            weights.Popularity, weights.Source, weights.Audio);
}

public sealed class GetDiscoverTuningHandler(
    IRankingModelRepository models,
    DiscoveryOptions options)
    : IRequestHandler<GetDiscoverTuningQuery, DiscoverTuningStatusDto>
{
    public async Task<DiscoverTuningStatusDto> Handle(
        GetDiscoverTuningQuery q, CancellationToken ct)
    {
        if (!options.TuningEnabled)
            return new DiscoverTuningStatusDto(false, null, 0, 0, null, null);

        var model = await models.GetAsync(q.UserId, ct);
        if (model is null ||
            !string.Equals(model.BaseVersion, DiscoverService.ModelVersion,
                StringComparison.Ordinal) ||
            model.UpdatedAtUtc < DateTime.UtcNow.AddDays(
                -Math.Clamp(options.TuningMaxAgeDays, 1, 3650)))
            return new DiscoverTuningStatusDto(false, null, 0, 0, null, null);

        DiscoverRankingWeights? weights = null;
        try
        {
            weights = JsonSerializer.Deserialize<DiscoverRankingWeights>(
                model.WeightsJson);
        }
        catch (JsonException)
        {
            // A corrupt model is treated as absent; defaults remain active.
        }
        if (weights is null)
            return new DiscoverTuningStatusDto(false, null, 0, 0, null, null);

        return new DiscoverTuningStatusDto(
            true,
            model.PresetName,
            model.Samples,
            model.Snapshots,
            model.UpdatedAtUtc,
            TuneDiscoverHandler.ToDto(weights));
    }
}
