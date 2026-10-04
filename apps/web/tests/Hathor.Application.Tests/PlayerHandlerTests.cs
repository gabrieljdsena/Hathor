using FluentAssertions;
using Hathor.Application.Player;
using Hathor.Application.Ports;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class PlayHandlerTests
{
    [Fact]
    public async Task Play_WithNoFileAndEmptyState_ReturnsStateWithoutPlaying()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var state = new PlaybackState { UserId = Guid.NewGuid() };
        playback.GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(state);

        var dto = await new PlayHandler(playback, songs, hub)
            .Handle(new PlayCommand(state.UserId, null, null, null), CancellationToken.None);

        dto.IsPlaying.Should().BeFalse();
        dto.CurrentSong.Should().BeNull();
        dto.FirstPlay.Should().BeTrue();
    }

    [Fact]
    public async Task Play_WithFile_StartsPlayingAndBroadcasts()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState { UserId = userId };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);
        PlayerEvents.Reset();

        var raised = new List<(Guid, string, long?)>();
        PlayerEvents.SongPlayed += (u, f, p) => raised.Add((u, f, p));

        var dto = await new PlayHandler(playback, songs, hub)
            .Handle(new PlayCommand(userId, "song.mp3", false, null), CancellationToken.None);

        try
        {
            dto.IsPlaying.Should().BeTrue();
            dto.FirstPlay.Should().BeFalse();
            raised.Should().ContainSingle().Which.Should().Be((userId, "song.mp3", null));
            await hub.Received(1).BroadcastStateAsync(
                userId, Arg.Any<Dtos.PlayerStateDto>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            PlayerEvents.Reset();
        }
    }

    [Fact]
    public async Task Next_WithRepeatAuto_ReplaysCurrent()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState
        {
            UserId = userId,
            CurrentFile = "song.mp3",
            Repeat = true,
            FirstPlay = false,
            IsPlaying = true,
        };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);

        var dto = await new NextHandler(playback, songs, hub)
            .Handle(new NextCommand(userId, Auto: true), CancellationToken.None);

        dto.IsPlaying.Should().BeTrue();
        state.CurrentFile.Should().Be("song.mp3");
        state.NextFiles.Should().BeEmpty();
    }
}

public sealed class ShuffleHandlerTests
{
    private static (IPlaybackStateRepository playback, ISongReadModel songs, IPlaybackHub hub, PlaybackState state)
        Setup(IReadOnlyList<string>? next = null)
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var state = new PlaybackState { UserId = Guid.NewGuid() };
        if (next is not null) state.NextFiles = next.ToList();
        playback.GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(state);
        songs.GetManyAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Dtos.SongDto>());
        songs.GetByFileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Dtos.SongDto?)null);
        return (playback, songs, hub, state);
    }

    // Reported flow: shuffle all-songs, then play an artist context, then
    // toggle shuffle off — the artist order (not all-songs) must return.
    [Fact]
    public async Task ShuffleOff_AfterContextSwitch_RestoresCurrentContext()
    {
        var (playback, songs, hub, state) = Setup(["s1", "s2", "s3", "s4", "s5", "s6"]);
        var userId = state.UserId;
        var shuffle = new PlayerPrefsHandler(playback, songs, hub);

        await shuffle.Handle(new ShuffleCommand(userId), CancellationToken.None);
        state.Shuffle.Should().BeTrue();
        state.UnshuffledFiles.Should().Equal("s1", "s2", "s3", "s4", "s5", "s6");

        // New playback context (artist view): must discard the all-songs
        // snapshot and shuffle the artist list itself.
        var artist = new List<string> { "a1", "a2", "a3", "a4", "a5", "a6" };
        await new QueueHandler(playback, songs, hub).Handle(
            new ReplaceQueueCommand(userId, "a1", artist, null,
                new Dtos.QueueSourceDto("artist", "A")), CancellationToken.None);
        // Current file excluded from next: the artist remainder is queued.
        var remainder = new List<string> { "a2", "a3", "a4", "a5", "a6" };
        state.UnshuffledFiles.Should().Equal(remainder);
        state.NextFiles.Should().BeEquivalentTo(remainder);

        await shuffle.Handle(new ShuffleCommand(userId), CancellationToken.None);
        state.Shuffle.Should().BeFalse();
        state.NextFiles.Should().Equal(remainder);
        state.UnshuffledFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplaceQueue_ShuffleOff_ClearsSnapshot()
    {
        var (playback, songs, hub, state) = Setup(["s1"]);
        state.UnshuffledFiles = ["stale"];
        await new QueueHandler(playback, songs, hub).Handle(
            new ReplaceQueueCommand(state.UserId, "n1", ["n1", "n2"], null,
                new Dtos.QueueSourceDto("album", "B")), CancellationToken.None);
        state.Shuffle.Should().BeFalse();
        state.UnshuffledFiles.Should().BeEmpty();
        state.NextFiles.Should().Equal("n2");
    }
}
