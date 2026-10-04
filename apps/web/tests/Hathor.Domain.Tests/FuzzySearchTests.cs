using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class FuzzySearchTests
{
    [Theory]
    [InlineData("Bohemian Rhapsody Queen", "bohemian")] // exact, case-insensitive
    [InlineData("Bohemian Rhapsody Queen", "Queen Rhapsody")] // tokens in any order
    [InlineData("Bohemian Rhapsody Queen", "Bohem")] // partial token
    [InlineData("", "")] // empty query matches everything
    [InlineData("Anything", "   ")]
    public void IsMatch_ExactOrPartial_ReturnsTrue(string haystack, string query) =>
        FuzzySearch.IsMatch(haystack, query).Should().BeTrue();

    [Theory]
    [InlineData("Bohemian Rhapsody", "Bohemain")] // substitution
    [InlineData("Bohemian Rhapsody", "Bohemain Rapsody")] // two typos, multi-token
    [InlineData("Bohemian Rhapsody", "Rhapsodyy")] // extra letter
    [InlineData("Bohemian Rhapsody", "Rhaposdy")] // missing letter
    [InlineData("Bohemian Rhapsody", "Rahpsody")] // transposition
    [InlineData("The Beatles", "Beetles")] // substitution in short token
    [InlineData("Nothing Else Matters", "Nothnig Else Maters")] // transposition + missing
    public void IsMatch_Typos_ReturnsTrue(string haystack, string query) =>
        FuzzySearch.IsMatch(haystack, query).Should().BeTrue();

    [Theory]
    [InlineData("Bohemian Rhapsody", "Stairway to Heaven")] // unrelated
    [InlineData("Bohemian Rhapsody", "Bhmn")] // too mangled
    [InlineData("Bohemian Rhapsody", "xyz")] // unknown short token
    [InlineData("Bohemian Rhapsody", "Bohemian Jazz")] // one token matches, one doesn't
    [InlineData("", "query")] // empty haystack never matches a query
    public void IsMatch_NonMatches_ReturnsFalse(string haystack, string query) =>
        FuzzySearch.IsMatch(haystack, query).Should().BeFalse();

    [Fact]
    public void IsMatch_Fields_AllowsTokensAcrossFields() =>
        FuzzySearch.IsMatch(["Bohemian Rhapsody", "Queen", "A Night at the Opera"], "quuen opera")
            .Should().BeTrue();

    [Fact]
    public void IsMatch_IgnoresDiacritics() =>
        FuzzySearch.IsMatch("Beyoncé", "beyonce").Should().BeTrue();

    [Fact]
    public void Score_ExactBeatsFuzzy()
    {
        var exact = FuzzySearch.Score("Bohemian Rhapsody", "Rhapsody");
        var fuzzy = FuzzySearch.Score("Bohemian Rhapsody", "Rapsody");
        exact.Should().Be(0);
        fuzzy.Should().NotBeNull();
        fuzzy!.Value.Should().BeGreaterThan(exact!.Value);
    }

    [Fact]
    public void Score_NoMatch_ReturnsNull() =>
        FuzzySearch.Score("Bohemian Rhapsody", "Stairway").Should().BeNull();
}
