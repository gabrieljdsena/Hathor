using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class TagMatcherTests
{
    [Fact]
    public void ExactTag_IsFullSimilarity()
    {
        TagMatcher.Similarity("Rock", "rock").Should().Be(1.0);
    }

    [Fact]
    public void BroadGenre_MatchesSubgenreByContainment()
    {
        TagMatcher.Similarity("Rock", "Alternative Rock")
            .Should().BeGreaterThanOrEqualTo(0.8);
        TagMatcher.Similarity("Pop", "Synth-Pop")
            .Should().BeGreaterThanOrEqualTo(0.8);
    }

    [Fact]
    public void GenreFamily_MatchesRelatedSubgenres()
    {
        TagMatcher.Similarity("Rock", "Shoegaze")
            .Should().BeGreaterThanOrEqualTo(0.6);
        TagMatcher.Similarity("Electronic", "Trip Hop")
            .Should().BeGreaterThanOrEqualTo(0.6);
        TagMatcher.Similarity("R&B", "Soul")
            .Should().BeGreaterThanOrEqualTo(0.6);
    }

    [Fact]
    public void UnrelatedGenres_DoNotMatch()
    {
        TagMatcher.Similarity("Rock", "Jazz").Should().Be(0);
        TagMatcher.Similarity("Classical", "Trap").Should().Be(0);
    }

    [Fact]
    public void SlashSeparatedGenres_AreSplitIntoVariants()
    {
        var variants = TagMatcher.Variants("Rock/Pop");
        variants.Should().BeEquivalentTo(["rock", "pop"]);
        TagMatcher.Similarity("Rock/Pop", "Indie Pop")
            .Should().BeGreaterThanOrEqualTo(0.8);
    }
}
