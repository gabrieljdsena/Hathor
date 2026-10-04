using FluentAssertions;
using Hathor.Domain.Services;

namespace Hathor.Domain.Tests;

public sealed class FileNamingServiceTests
{
    [Theory]
    [InlineData("AC/DC: Back*In?Black<>|", "ACDC BackInBlack")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("", "Unknown")]
    [InlineData("...", "Unknown")]
    public void SafeStem_StripsIllegalChars(string input, string expected) =>
        FileNamingService.SafeStem(input).Should().Be(expected);

    [Fact]
    public void SafeStem_CapsAt150Chars() =>
        FileNamingService.SafeStem(new string('a', 200)).Should().HaveLength(150);

    [Fact]
    public void SafeMp3FileName_AppendsExtension() =>
        FileNamingService.SafeMp3FileName("Hello").Should().Be("Hello.mp3");

    [Fact]
    public void ResolveCollision_AppendsCounter()
    {
        var existing = new HashSet<string> { "Title.mp3", "Title (1).mp3" };
        FileNamingService.ResolveCollision("Title.mp3", existing.Contains)
            .Should().Be("Title (2).mp3");
    }

    [Fact]
    public void ResolveFinalName_PinnedTargetWinsVerbatim()
    {
        // Pull downloads must land on the exact remote filename even if a
        // file with that name already exists (adopted, not renamed).
        var existing = new HashSet<string> { "episode-123.mp3" };
        FileNamingService.ResolveFinalName("episode-123.mp3", "Other Title.mp3", existing.Contains)
            .Should().Be("episode-123.mp3");
    }

    [Fact]
    public void ResolveFinalName_WithoutTargetFallsBackToCollision()
    {
        var existing = new HashSet<string> { "Title.mp3" };
        FileNamingService.ResolveFinalName(null, "Title.mp3", existing.Contains)
            .Should().Be("Title (1).mp3");
        FileNamingService.ResolveFinalName("  ", "Title.mp3", existing.Contains)
            .Should().Be("Title (1).mp3");
    }

    [Fact]
    public void ResolveCollision_KeepsNameWhenFree() =>
        FileNamingService.ResolveCollision("Fresh.mp3", _ => false).Should().Be("Fresh.mp3");
}

public sealed class DailyMixGeneratorTests
{
    [Fact]
    public void Build_EmptyLibrary_ReturnsEmpty() =>
        DailyMixGenerator.Build([], [], new Random(42)).Should().BeEmpty();

    [Fact]
    public void Build_SmallLibrary_ReturnsAllFiles()
    {
        var files = Enumerable.Range(0, 10).Select(i => $"s{i}.mp3").ToList();
        DailyMixGenerator.Build(files, [], new Random(42))
            .Should().HaveCount(10).And.OnlyContain(f => files.Contains(f));
    }

    [Fact]
    public void Build_LargeLibrary_RespectsQuotasAndSize()
    {
        var files = Enumerable.Range(0, 200).Select(i => $"s{i:D3}.mp3").ToList();
        var mix = DailyMixGenerator.Build(files, files, new Random(42));
        mix.Should().HaveCount(50);
        mix.Should().OnlyHaveUniqueItems();
        // 2 from top 10, 5 from 11-25, 13 from 26-70
        mix.Count(files.Take(10).Contains).Should().Be(2);
        mix.Count(files.Skip(10).Take(15).Contains).Should().Be(5);
        mix.Count(files.Skip(25).Take(45).Contains).Should().Be(13);
    }

    [Fact]
    public void Build_RestPrefersDiscoveryOutsideTop70()
    {
        var files = Enumerable.Range(0, 200).Select(i => $"s{i:D3}.mp3").ToList();
        var mix = DailyMixGenerator.Build(files, files, new Random(7));
        // 30 discovery slots must come from outside the top 70
        mix.Count(f => !files.Take(70).Contains(f)).Should().Be(30);
    }
}

public sealed class LyricsCleaningTests
{
    [Fact]
    public void CleanTrackArtist_SplitsDashWhenArtistUnknown()
    {
        var (track, artist) = LyricsCleaning.CleanTrackArtist("Artist - Song", "Unknown");
        track.Should().Be("Song");
        artist.Should().Be("Artist");
    }

    [Fact]
    public void CleanTrackArtist_StripsRemasterQualifier()
    {
        var (track, _) = LyricsCleaning.CleanTrackArtist("Song (Remastered 2020)", "Someone");
        track.Should().Be("Song");
    }

    [Theory]
    [InlineData("Unknown", true)]
    [InlineData("unknown artist", true)]
    [InlineData("", true)]
    [InlineData("Real Artist", false)]
    public void IsUnknownArtist_MatchesDesktopRule(string artist, bool expected) =>
        LyricsCleaning.IsUnknownArtist(artist).Should().Be(expected);

    [Fact]
    public void AcceptTrackOnlyCandidate_RequiresExactMatchAndSkipsInstrumentals()
    {
        LyricsCleaning.AcceptTrackOnlyCandidate("My Song", "my  song", false).Should().BeTrue();
        LyricsCleaning.AcceptTrackOnlyCandidate("My Song", "My Song", true).Should().BeFalse();
        LyricsCleaning.AcceptTrackOnlyCandidate("My Song", "Other Song", false).Should().BeFalse();
    }
}
