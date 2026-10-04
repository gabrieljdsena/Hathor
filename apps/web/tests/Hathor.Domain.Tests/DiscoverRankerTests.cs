using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class DiscoverRankerTests
{
    private static DiscoverCandidate Cand(
        string title, string artist, string source = DiscoverSources.Artist) =>
        new(title, artist, "Album", "2020", "Rock", "http://art", source);

    [Fact]
    public void Rank_DropsOwnedLibrary_ByTitleAndArtist()
    {
        var taste = new List<(string, long)> { ("Pink Floyd", 10) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Wish You Were Here", "Pink Floyd"),
            Cand("Comfortably Numb", "Pink Floyd"),
        };
        var library = new List<(string, string)> { ("Wish You Were Here", "Pink Floyd") };

        var ranked = DiscoverRanker.Rank(taste, candidates, library, []);

        ranked.Should().HaveCount(1);
        ranked[0].Title.Should().Be("Comfortably Numb");
    }

    [Fact]
    public void Rank_DropsOwnedLibrary_IgnoringRemasterSuffixAndCase()
    {
        var taste = new List<(string, long)> { ("Nirvana", 5) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Smells Like Teen Spirit (Remastered 2011)", "NIRVANA"),
        };
        var library = new List<(string, string)> { ("Smells Like Teen Spirit", "Nirvana") };

        DiscoverRanker.Rank(taste, candidates, library, []).Should().BeEmpty();
    }

    [Fact]
    public void Rank_DedupesSameSong_FromTwoSources()
    {
        var taste = new List<(string, long)> { ("M83", 7) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Midnight City", "M83", DiscoverSources.Artist),
            Cand("Midnight City", "M83", DiscoverSources.Chart),
            Cand("Midnight City", "M83", DiscoverSources.Llm),
        };

        var ranked = DiscoverRanker.Rank(taste, candidates, [], []);

        ranked.Should().HaveCount(1);
        ranked[0].Source.Should().Be(DiscoverSources.Artist);
    }

    [Theory]
    [InlineData("Midnight City - Single", "Midnight City")]
    [InlineData("Song - Remastered 2015", "Song")]
    [InlineData("Don't Stop Believin'", "Dont Stop Believin")]
    [InlineData("Song feat. Guest", "Song")]
    [InlineData("Song featuring Guest", "Song")]
    [InlineData("R&B Nights", "RB Nights")]
    public void Rank_DropsOwnedLibrary_AcrossTitleVariants(string candidate, string owned)
    {
        var taste = new List<(string, long)> { ("A", 5) };
        var candidates = new List<DiscoverCandidate> { Cand(candidate, "A") };
        var library = new List<(string, string)> { (owned, "A") };

        DiscoverRanker.Rank(taste, candidates, library, []).Should().BeEmpty();
    }

    [Fact]
    public void Rank_KeepsDistinctTitles_Apart()
    {
        var taste = new List<(string, long)> { ("A", 5), ("B", 4), ("C", 3) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Time", "A"),
            Cand("Times", "B"),
            Cand("Song 2", "C"),
        };

        DiscoverRanker.Rank(taste, candidates, [], []).Should().HaveCount(3);
    }

    [Fact]
    public void Rank_DoesNotStripBareFt_WithoutDot()
    {
        // "50 Ft Queenie" must survive: only feat./ft./featuring collapse.
        var taste = new List<(string, long)> { ("PJ Harvey", 5) };
        var candidates = new List<DiscoverCandidate> { Cand("50 Ft Queenie", "PJ Harvey") };
        var library = new List<(string, string)> { ("50 Queenie", "PJ Harvey") };

        DiscoverRanker.Rank(taste, candidates, library, []).Should().HaveCount(1);
    }

    [Fact]
    public void Rank_DropsInFlightDownloads()
    {
        var taste = new List<(string, long)> { ("M83", 7) };
        var candidates = new List<DiscoverCandidate> { Cand("Midnight City", "M83") };
        var inFlight = new List<(string, string)> { ("Midnight City", "M83") };

        DiscoverRanker.Rank(taste, candidates, [], inFlight).Should().BeEmpty();
    }

    [Fact]
    public void Rank_CapsTwoPerArtist_KeepingHighestAffinity()
    {
        var taste = new List<(string, long)> { ("A", 10), ("B", 1) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("A1", "A"), Cand("A2", "A"), Cand("A3", "A"), Cand("B1", "B"),
        };

        var ranked = DiscoverRanker.Rank(taste, candidates, [], []);

        ranked.Select(i => i.Title).Should().BeEquivalentTo("A1", "A2", "B1");
    }

    [Fact]
    public void Rank_OrdersByScore_AffinityThenSource()
    {
        var taste = new List<(string, long)> { ("Top", 100), ("Mid", 10) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Chart Hit", "Nobody", DiscoverSources.Chart),
            Cand("Mid Song", "Mid"),
            Cand("Top Song", "Top"),
        };

        var ranked = DiscoverRanker.Rank(taste, candidates, [], []);

        ranked.Select(i => i.Title).Should().ContainInOrder("Top Song", "Mid Song", "Chart Hit");
        ranked.Should().OnlyContain(i => i.Score >= 0 && i.Score <= 1);
    }

    [Fact]
    public void Rank_EmptyTaste_StillReturnsChartPicks()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Chart Hit", "Nobody", DiscoverSources.Chart),
        };

        var ranked = DiscoverRanker.Rank([], candidates, [], []);

        ranked.Should().HaveCount(1);
    }

    [Fact]
    public void Rank_SkipsBlankTitleOrArtist()
    {
        var taste = new List<(string, long)> { ("A", 3) };
        var candidates = new List<DiscoverCandidate>
        {
            Cand("", "A"), Cand("T", ""), Cand("T", "A"),
        };

        DiscoverRanker.Rank(taste, candidates, [], []).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Pink Floyd", "pink floyd", true)]
    [InlineData("M83", "M83 ", true)]
    [InlineData("Nirvana", "Pearl Jam", false)]
    public void IsSameArtist_IgnoresCaseAndSpace(string a, string b, bool expected) =>
        DiscoverRanker.IsSameArtist(a, b).Should().Be(expected);
}
