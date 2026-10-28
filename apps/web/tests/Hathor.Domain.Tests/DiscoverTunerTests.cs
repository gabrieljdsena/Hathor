using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class DiscoverTunerTests
{
    private static DiscoverItemFeatures Features(
        double similarity = 0, double tag = 0, double affinity = 0,
        double popularity = 0, double source = 0.5, double audio = 0.5) =>
        new(similarity, tag, affinity, popularity, source,
            0, 0, 0, 0, 0, 0.5, 0, 0.5, audio);

    private static DiscoverEvaluationItem Item(
        int rank, string key, string @event, DiscoverItemFeatures features) =>
        new("2026-10-01", rank, key, DiscoverSources.Similar, "Artist",
            null, features, @event);

    [Fact]
    public void Tune_AppliesPreset_WhenItImprovesRanking()
    {
        // Current order puts the dismissal first and the download second.
        // tag-heavy should move the download to rank 1.
        var items = new List<DiscoverEvaluationItem>
        {
            Item(1, "wrong", DiscoverFeedbackEvents.Dismiss,
                Features(tag: 0, popularity: 1)),
            Item(2, "right", DiscoverFeedbackEvents.Download,
                Features(tag: 1, popularity: 0)),
        };

        var result = DiscoverEvaluator.Tune(
            items, 30, 10, 1, minSnapshots: 1, minLabels: 2, margin: 0.02);

        result.InsufficientData.Should().BeFalse();
        result.Applied.Should().BeTrue();
        result.Preset.Should().NotBeNull();
        result.BestScore.Should().BeGreaterThan(result.CurrentScore);
        result.Weights.Should().NotBeNull();
        result.Weights!.Tag.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Tune_InsufficientData_DoesNotApply()
    {
        var result = DiscoverEvaluator.Tune(
            [], 30, 10, 0, minSnapshots: 1, minLabels: 2, margin: 0.02);

        result.InsufficientData.Should().BeTrue();
        result.Applied.Should().BeFalse();
        result.Weights.Should().BeNull();
    }
}
