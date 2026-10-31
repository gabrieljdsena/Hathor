using System.Text.Json;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// Default weights unless the user has a fresh, evaluation-tuned model.
// A tuned model changes the cache version so it takes effect immediately.
public sealed class RankingOptionsProvider(
    IRankingModelRepository models,
    DiscoveryOptions options,
    ILogger<RankingOptionsProvider> log) : IRankingOptionsProvider
{
    public async Task<RankingOptionsSnapshot> GetAsync(
        Guid userId, CancellationToken ct = default)
    {
        var defaults = options.ToRankingOptions();
        var baseVersion = DiscoverService.ModelVersion;
        if (!options.TuningEnabled)
            return new RankingOptionsSnapshot(defaults, baseVersion, null);

        try
        {
            var model = await models.GetAsync(userId, ct);
            if (model is null) return new RankingOptionsSnapshot(defaults, baseVersion, null);
            if (!string.Equals(model.BaseVersion, baseVersion, StringComparison.Ordinal))
                return new RankingOptionsSnapshot(defaults, baseVersion, null);
            if (model.UpdatedAtUtc < DateTime.UtcNow.AddDays(
                    -Math.Clamp(options.TuningMaxAgeDays, 1, 3650)))
                return new RankingOptionsSnapshot(defaults, baseVersion, null);

            var weights = JsonSerializer.Deserialize<DiscoverRankingWeights>(
                model.WeightsJson);
            if (weights is null)
                return new RankingOptionsSnapshot(defaults, baseVersion, null);

            var tuned = new DiscoverRankingOptions
            {
                SimilarityWeight = weights.Similarity,
                TagWeight = weights.Tag,
                AffinityWeight = weights.Affinity,
                PopularityWeight = weights.Popularity,
                SourceWeight = weights.Source,
                AudioWeight = weights.Audio,
                MmrLambda = defaults.MmrLambda,
                MaxItems = defaults.MaxItems,
                MaxPerArtist = defaults.MaxPerArtist,
                PopularityMin = defaults.PopularityMin,
                PopularityMax = defaults.PopularityMax,
                LikedArtistBoost = defaults.LikedArtistBoost,
                LikedSeedBoost = defaults.LikedSeedBoost,
                EngagedArtistBoost = defaults.EngagedArtistBoost,
            };
            var version = $"{baseVersion}:tune-{model.UpdatedAtUtc.Ticks}";
            return new RankingOptionsSnapshot(tuned, version, model.PresetName);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Tuned ranking model unavailable — using defaults");
            return new RankingOptionsSnapshot(defaults, baseVersion, null);
        }
    }
}
