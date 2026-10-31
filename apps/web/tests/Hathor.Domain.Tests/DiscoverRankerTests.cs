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

    private static TasteProfile Taste(params (string Artist, double Weight)[] artists) =>
        new(artists.Select(a => new TasteArtist(a.Artist, (long)(a.Weight * 10), a.Weight)).ToList(),
            [], []);

    private static TasteProfile TasteWithTags(params string[] tags) => new([], [], tags);

    [Fact]
    public void Rank_PrefersSimilarOpenDataSource_OverChart()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Chart Hit", "Nobody", DiscoverSources.Chart),
            Cand("Deep Cut", "Slowdive", DiscoverSources.Similar) with
            {
                Similarity = 1.0,
                SeedArtist = "M83",
            },
        };

        var ranked = DiscoverRanker.Rank(Taste(("M83", 10)), candidates, [], []);

        ranked[0].Title.Should().Be("Deep Cut");
        ranked[0].Reason.Should().Be("Because you play M83");
    }

    [Fact]
    public void Rank_DismissedItem_IsExcluded()
    {
        var candidates = new List<DiscoverCandidate> { Cand("Midnight City", "M83") };
        var dismissed = new List<(string, string)> { ("Midnight City", "M83") };

        DiscoverRanker.Rank(Taste(("M83", 10)), candidates, [], [], dismissed)
            .Should().BeEmpty();
    }

    [Fact]
    public void Rank_LongTailPopularity_BeatsBlockbuster()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Blockbuster", "A", DiscoverSources.Similar) with
            {
                Similarity = 1.0,
                Popularity = 50_000_000,
            },
            Cand("Long Tail", "B", DiscoverSources.Similar) with
            {
                Similarity = 1.0,
                Popularity = 100_000,
            },
        };

        var ranked = DiscoverRanker.Rank(Taste(("Seed", 10)), candidates, [], []);

        ranked[0].Title.Should().Be("Long Tail");
    }

    private static DiscoverFeedbackSignals Signals(
        IEnumerable<string>? dismissedKeys = null,
        IEnumerable<string>? dismissedArtists = null,
        IEnumerable<string>? likedArtists = null,
        IEnumerable<string>? likedSeeds = null,
        IEnumerable<string>? engagedArtists = null,
        IReadOnlyDictionary<string, double>? sourcePriors = null,
        IReadOnlyDictionary<string, double>? artistBoosts = null,
        IReadOnlyDictionary<string, double>? seedBoosts = null) =>
        new(
            new HashSet<string>(dismissedKeys ?? [], StringComparer.Ordinal),
            new HashSet<string>(dismissedArtists ?? [], StringComparer.Ordinal),
            new HashSet<string>(likedArtists ?? [], StringComparer.Ordinal),
            new HashSet<string>(likedSeeds ?? [], StringComparer.Ordinal),
            new HashSet<string>(engagedArtists ?? [], StringComparer.Ordinal),
            sourcePriors ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            artistBoosts ?? new Dictionary<string, double>(StringComparer.Ordinal),
            seedBoosts ?? new Dictionary<string, double>(StringComparer.Ordinal));

    [Fact]
    public void Rank_FeedbackDismissesArtist()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Alison", "Slowdive"),
            Cand("Midnight City", "M83"),
        };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [],
            feedback: Signals(dismissedArtists: [MediaKeys.Normalize("Slowdive")]));

        ranked.Should().ContainSingle(i => i.Title == "Midnight City");
    }

    [Fact]
    public void Rank_LikedSeed_BoostsMatchingCandidate()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Other Song", "Other Artist") with { SeedArtist = "Other" },
            Cand("Alison", "Slowdive") with { SeedArtist = "M83" },
        };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [],
            feedback: Signals(likedSeeds: [MediaKeys.Normalize("M83")]));

        ranked[0].Title.Should().Be("Alison");
    }

    [Fact]
    public void Rank_LikedArtist_BoostsMatchingCandidate()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Other Song", "Other Artist"),
            Cand("Alison", "Slowdive"),
        };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [],
            feedback: Signals(likedArtists: [MediaKeys.Normalize("Slowdive")]));

        ranked[0].Title.Should().Be("Alison");
    }

    [Fact]
    public void Rank_ReturnsUpToFiftyItems()
    {
        var candidates = new List<DiscoverCandidate>();
        for (var i = 0; i < 30; i++)
        {
            candidates.Add(Cand($"Song {i}a", $"Artist {i}"));
            candidates.Add(Cand($"Song {i}b", $"Artist {i}"));
        }

        var ranked = DiscoverRanker.Rank(Taste(("Seed", 10)), candidates, [], []);

        ranked.Should().HaveCount(DiscoverRanker.MaxItems);
        ranked.GroupBy(i => i.Artist).Should().OnlyContain(g => g.Count() <= 2);
    }

    [Fact]
    public void Rank_FuzzyGenreMatch_BeatsUnrelatedTag()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Alt Rock", "Artist A") with { Tags = ["alternative rock"] },
            Cand("Jazz", "Artist B") with { Tags = ["jazz"] },
        };

        var ranked = DiscoverRanker.Rank(TasteWithTags("Rock"), candidates, [], []);

        ranked[0].Title.Should().Be("Alt Rock");
    }

    [Fact]
    public void Rank_PopularityFloor_PenalizesObscureTracks()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Obscure", "Artist A") with { Popularity = 10 },
            Cand("Popular", "Artist B") with { Popularity = 100_000 },
        };
        var options = new DiscoverRankingOptions { PopularityFloor = 1_000 };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [], options: options);

        ranked[0].Title.Should().Be("Popular");
    }

    [Fact]
    public void Rank_PopularityProfile_PrefersUserBand()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Mainstream", "Artist A") with { Popularity = 50_000_000 },
            Cand("In Band", "Artist B") with { Popularity = 100_000 },
        };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [],
            popularity: new PopularityProfile(5.0, 5));

        ranked[0].Title.Should().Be("In Band");
    }

    [Fact]
    public void Rank_AudioSimilarity_ChangesOrder()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("No Audio", "Artist A"),
            Cand("Audio Match", "Artist B") with { AudioSimilarity = 1.0 },
        };

        var ranked = DiscoverRanker.Rank(Taste(("Seed", 10)), candidates, [], []);

        ranked[0].Title.Should().Be("Audio Match");
    }

    [Fact]
    public void Rank_LearnedSourcePrior_CanFlipStaticOrdering()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("Graph Song", "A", DiscoverSources.Graph),
            Cand("Tag Song", "B", DiscoverSources.Tag),
        };
        var priors = new Dictionary<string, double>
        {
            [DiscoverSources.Graph] = 0.1,
            [DiscoverSources.Tag] = 1.0,
        };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [],
            feedback: Signals(sourcePriors: priors));

        ranked[0].Title.Should().Be("Tag Song");
    }

    [Fact]
    public void Rank_LearnedArtistBoost_ReordersCandidates()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("A Song", "A"),
            Cand("B Song", "B"),
        };
        var boosts = new Dictionary<string, double>
        {
            [MediaKeys.Normalize("A")] = 0.10,
            [MediaKeys.Normalize("B")] = -0.10,
        };

        var ranked = DiscoverRanker.Rank(
            Taste(("Seed", 10)), candidates, [], [],
            feedback: Signals(artistBoosts: boosts));

        ranked[0].Title.Should().Be("A Song");
    }

    [Fact]
    public void Rank_Mmr_KeepsArtistDiversity()
    {
        var candidates = new List<DiscoverCandidate>
        {
            Cand("A1", "A", DiscoverSources.Similar) with { Similarity = 1.0 },
            Cand("A2", "A", DiscoverSources.Similar) with { Similarity = 1.0 },
            Cand("B1", "B", DiscoverSources.Similar) with { Similarity = 0.9 },
        };

        var ranked = DiscoverRanker.Rank(Taste(("Seed", 10)), candidates, [], []);

        ranked.Select(i => i.Artist).Distinct().Should().HaveCount(2);
        ranked.Select(i => i.Title).Should().Contain(["A1", "B1"]);
    }
}
