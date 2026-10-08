using FluentAssertions;
using Hathor.Application.Lyrics;

namespace Hathor.Application.Tests;

// Chapter-lyrics sync guard (DB-free): per-chapter cache rows must never
// cross to the desktop (phantom rows that round-trip forever).
public sealed class SyncGuardTests
{
    [Theory]
    [InlineData("ep.mp3::chapter:3", true)]
    [InlineData("ep.mp3::chapter:44", true)]
    [InlineData("s.mp3", false)]
    [InlineData("ep.mp3", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsChapterLyricsRow_MatchesOnlyChapterKeys(string? file, bool expected) =>
        SyncedLyricsGuards.IsChapterLyricsRow(file).Should().Be(expected);
}
