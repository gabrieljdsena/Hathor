using FluentAssertions;
using Hathor.Domain.Playback;

namespace Hathor.Domain.Tests;

public sealed class PlaybackStateQueueTests
{
    private static PlaybackState New() => new() { UserId = Guid.NewGuid() };

    [Fact]
    public void JumpToIndex_SlicesAfterIndex()
    {
        var s = New();
        s.NextFiles = ["a", "b", "c", "d"];
        s.JumpToIndex(1);
        s.NextFiles.Should().Equal("c", "d");
    }

    [Fact]
    public void JumpToIndex_OutOfRange_NoOp()
    {
        var s = New();
        s.NextFiles = ["a"];
        s.JumpToIndex(5);
        s.NextFiles.Should().Equal("a");
    }

    [Fact]
    public void Reorder_MovesItem()
    {
        var s = New();
        s.NextFiles = ["a", "b", "c"];
        s.Reorder(0, 2);
        s.NextFiles.Should().Equal("b", "c", "a");
        s.IsCustomQueue.Should().BeTrue();
    }

    [Fact]
    public void ClearQueue_ResetsFlags()
    {
        var s = New();
        s.NextFiles = ["a"];
        s.IsCustomQueue = true;
        s.FallbackToGeneralList = true;
        s.ClearQueue();
        s.NextFiles.Should().BeEmpty();
        s.IsCustomQueue.Should().BeFalse();
        s.FallbackToGeneralList.Should().BeFalse();
    }

    [Fact]
    public void ClearQueue_DropsUnshuffleSnapshot()
    {
        var s = New();
        s.NextFiles = ["a", "b"];
        s.UnshuffledFiles = ["x", "y"];
        s.ClearQueue();
        s.UnshuffledFiles.Should().BeEmpty();
    }

    [Fact]
    public void Advance_EmptyingQueue_DropsUnshuffleSnapshot()
    {
        var s = New();
        s.CurrentFile = "cur";
        s.NextFiles = ["last"];
        s.UnshuffledFiles = ["cur", "last"];
        s.Advance().Should().Be("last");
        s.NextFiles.Should().BeEmpty();
        s.UnshuffledFiles.Should().BeEmpty();
    }

    [Fact]
    public void Advance_NonEmpty_KeepsUnshuffleSnapshot()
    {
        var s = New();
        s.CurrentFile = "cur";
        s.NextFiles = ["a", "b"];
        s.UnshuffledFiles = ["cur", "a", "b"];
        s.Advance().Should().Be("a");
        s.UnshuffledFiles.Should().Equal("cur", "a", "b");
    }

    [Fact]
    public void SetQueue_ShuffleOff_ClearsStaleSnapshot()
    {
        var s = New();
        s.Shuffle = false;
        s.UnshuffledFiles = ["stale", "list"];
        s.SetQueue(["n1", "n2"], [], new QueueSource("artist", "A"), false, false);
        s.UnshuffledFiles.Should().BeEmpty();
        s.NextFiles.Should().Equal("n1", "n2");
    }

    [Fact]
    public void SetQueue_ShuffleOn_SnapshotsAndShufflesNewContext()
    {
        var s = New();
        s.Shuffle = true;
        s.UnshuffledFiles = ["stale", "all", "songs"];
        var fresh = new List<string> { "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8" };
        s.SetQueue(fresh, [], new QueueSource("artist", "A"), false, false, rng: new Random(42));
        // Stale all-songs snapshot is gone; the artist context is snapshotted…
        s.UnshuffledFiles.Should().Equal("a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8");
        // …and the playable queue holds the same songs in shuffled order.
        s.NextFiles.Should().BeEquivalentTo("a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8");
        s.NextFiles.SequenceEqual(new[] { "a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8" })
            .Should().BeFalse("a seeded shuffle must reorder 8 items");
        s.Source.Should().Be(new QueueSource("artist", "A"));
    }

    [Fact]
    public void ShuffleInPlace_IsAPermutation()
    {
        var list = new List<string> { "a", "b", "c", "d", "e" };
        PlaybackState.ShuffleInPlace(list, new Random(7));
        list.Should().BeEquivalentTo("a", "b", "c", "d", "e");
    }

    [Fact]
    public void Advance_ShiftsHeadToCurrentAndPushesPrev()
    {
        var s = New();
        s.CurrentFile = "now";
        s.NextFiles = ["n1", "n2"];
        s.Advance().Should().Be("n1");
        s.CurrentFile.Should().Be("n1");
        s.NextFiles.Should().Equal("n2");
        s.PrevFiles.Should().Equal("now");
    }

    [Fact]
    public void Advance_EmptyQueue_ReturnsNull()
    {
        var s = New();
        s.CurrentFile = "now";
        s.Advance().Should().BeNull();
        s.CurrentFile.Should().Be("now");
    }

    [Fact]
    public void Rewind_RestoresPrevious()
    {
        var s = New();
        s.CurrentFile = "now";
        s.PrevFiles = ["p1"];
        s.Rewind().Should().Be("p1");
        s.CurrentFile.Should().Be("p1");
        s.NextFiles.Should().Equal("now");
        s.PrevFiles.Should().BeEmpty();
    }

    [Fact]
    public void EstimatedPosition_PausedReturnsPausePosition()
    {
        var s = New();
        s.FirstPlay = false;
        s.IsPlaying = false;
        s.PausePositionSec = 42;
        s.EstimatedPositionSec(DateTime.UtcNow).Should().Be(42);
    }

    [Fact]
    public void EstimatedPosition_FirstPlayIsZero()
    {
        var s = New();
        s.EstimatedPositionSec(DateTime.UtcNow).Should().Be(0);
    }

    [Fact]
    public void Mute_RestoresPreviousVolumeOnUnmute()
    {
        var s = New();
        s.Volume = 0.7;
        s.Mute();
        s.Volume.Should().Be(0);
        s.Unmute();
        s.Volume.Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public void QueueSource_ParseRejectsInvalidTypes()
    {
        QueueSource.Parse(null).Should().BeNull();
        QueueSource.Parse("not json").Should().BeNull();
        QueueSource.Parse("""{"Type":"nope","Id":null}""").Should().BeNull();
        QueueSource.Parse("""{"Type":"playlist","Id":"5"}""")
            .Should().Be(new QueueSource("playlist", "5"));
    }
}
