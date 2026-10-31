using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class DiscoverInsightCalculatorTests
{
    [Fact]
    public void Build_AggregatesFeedbackAndPlayback()
    {
        var facts = new List<DiscoverOutcomeFact>
        {
            new(DiscoverSources.Similar, DiscoverFeedbackEvents.Download, "M83"),
            new(DiscoverSources.Similar, DiscoverFeedbackEvents.Download, "M83"),
            new(DiscoverSources.Graph, DiscoverFeedbackEvents.Dismiss, "Slowdive"),
            new(DiscoverSources.Graph, DiscoverFeedbackEvents.Dismiss, "Slowdive"),
            new(DiscoverSources.Similar, DiscoverFeedbackEvents.Preview, "M83"),
            new(DiscoverSources.Tag, DiscoverFeedbackEvents.Click, null),
        };

        var summary = DiscoverInsightCalculator.Build(facts, 90, 3, 1);

        summary.WindowDays.Should().Be(90);
        summary.Downloads.Should().Be(2);
        summary.Dismissals.Should().Be(2);
        summary.Previews.Should().Be(1);
        summary.Clicks.Should().Be(1);
        summary.ConversionRate.Should().BeApproximately(0.5, 0.001);
        summary.PlaybackCompletionRate.Should().BeApproximately(0.75, 0.001);
        summary.UniqueArtists.Should().Be(2);

        var similar = summary.Sources.Single(s => s.Source == DiscoverSources.Similar);
        similar.ConversionRate.Should().Be(1.0);
        similar.LearnedPrior.Should().BeApproximately(0.75, 0.001);

        var graph = summary.Sources.Single(s => s.Source == DiscoverSources.Graph);
        graph.ConversionRate.Should().Be(0.0);
        graph.LearnedPrior.Should().BeApproximately(0.25, 0.001);
    }

    [Fact]
    public void Build_NoOutcomes_IsNeutral()
    {
        var summary = DiscoverInsightCalculator.Build([], 30, 0, 0);

        summary.ConversionRate.Should().Be(0.5);
        summary.PlaybackCompletionRate.Should().Be(0.5);
        summary.Sources.Should().BeEmpty();
    }
}
