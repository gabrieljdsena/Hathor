using FluentAssertions;
using Hathor.Application.Metadata;
using Hathor.Application.Player;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

[Collection("PlayerEventsSerial")]
public sealed class PlayHandlerTests
{
    // Track-change hook with no stashed edits: oldFile is null/unchanged in
    // these tests, so the applier never fires — substitutes only.
    private static PendingMetadataApplier NoOpApplier() =>
        new(
            Substitute.For<IPendingEditRepository>(),
            Substitute.For<IMetadataWriter>(),
            Substitute.For<ILibraryStorage>(),
            Substitute.For<ISongRecordRepository>(),
            Substitute.For<ISongReadModel>(),
            Substitute.For<IPodcastRecordRepository>(),
            Substitute.For<IPodcastReadModel>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PendingMetadataApplier>>());
    [Fact]
    public async Task Play_WithNoFileAndEmptyState_ReturnsStateWithoutPlaying()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var state = new PlaybackState { UserId = Guid.NewGuid() };
        playback.GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(state);

        var dto = await new PlayHandler(playback, songs, NoOpApplier(), hub)
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

        var dto = await new PlayHandler(playback, songs, NoOpApplier(), hub)
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

        var dto = await new NextHandler(playback, songs, NoOpApplier(), hub)
            .Handle(new NextCommand(userId, Auto: true), CancellationToken.None);

        dto.IsPlaying.Should().BeTrue();
        state.CurrentFile.Should().Be("song.mp3");
        state.NextFiles.Should().BeEmpty();
    }

    private static Dtos.SongDto SongRow(string file, double duration) =>
        new(file, "Artist", "Title", "Album", "2026", duration, null, null);

    private static void SongsResolve(
        ISongReadModel songs, string? currentFile, double duration, out Dtos.SongDto? current)
    {
        current = currentFile is null ? null : SongRow(currentFile, duration);
        var captured = current;
        songs.GetByFileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(captured);
        songs.GetManyAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Dtos.SongDto>());
    }

    [Fact]
    public async Task Next_WithMismatchedExpectedFile_ConvergesWithoutAdvancing()
    {
        // Crossfade handoff racing a manual transport: the queue already
        // moved on, so the fade must not consume a second advance.
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState
        {
            UserId = userId,
            CurrentFile = "b.mp3",
            FirstPlay = false,
            IsPlaying = true,
            NextFiles = new List<string> { "c.mp3" },
        };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);
        SongsResolve(songs, "b.mp3", 200, out _);

        var dto = await new NextHandler(playback, songs, NoOpApplier(), hub)
            .Handle(new NextCommand(userId, ExpectedFile: "a.mp3"), CancellationToken.None);

        state.CurrentFile.Should().Be("b.mp3");
        state.NextFiles.Should().ContainSingle().Which.Should().Be("c.mp3");
        dto.CurrentSong?.File.Should().Be("b.mp3");
    }

    [Fact]
    public async Task Next_WithMatchingExpectedFile_AdvancesNormally()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState
        {
            UserId = userId,
            CurrentFile = "b.mp3",
            FirstPlay = false,
            IsPlaying = true,
            NextFiles = new List<string> { "c.mp3" },
        };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);
        SongsResolve(songs, "c.mp3", 200, out _);

        var dto = await new NextHandler(playback, songs, NoOpApplier(), hub)
            .Handle(new NextCommand(userId, ExpectedFile: "b.mp3"), CancellationToken.None);

        state.CurrentFile.Should().Be("c.mp3");
        dto.CurrentSong?.File.Should().Be("c.mp3");
    }

    [Fact]
    public async Task Next_WithExpectedFileWhilePaused_AdvancesButStaysPaused()
    {
        // Pause landing between fade start and its server advance: the
        // queue still moves, but playback must not resurrect.
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState
        {
            UserId = userId,
            CurrentFile = "a.mp3",
            FirstPlay = false,
            IsPlaying = false,
            PositionOffsetSec = 0,
            LastPlayUtc = DateTime.UtcNow,
            NextFiles = new List<string> { "b.mp3" },
        };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);
        SongsResolve(songs, "b.mp3", 200, out _);

        var dto = await new NextHandler(playback, songs, NoOpApplier(), hub)
            .Handle(new NextCommand(userId, ExpectedFile: "a.mp3"), CancellationToken.None);

        state.CurrentFile.Should().Be("b.mp3");
        state.NextFiles.Should().BeEmpty();
        dto.IsPlaying.Should().BeFalse();
        dto.PositionSec.Should().BeLessThan(1);
    }
    [Fact]
    public async Task Next_WithEmptyQueue_StoppingOnSameFile_AppliesStashedEdit()
    {
        // Deferred edit for the playing file (PATCH stashes while current):
        // stopping on the same file must flush it — no track change will
        // ever come to trigger the normal hook, and the finished stream is
        // done with the old bytes. The stop response already carries the
        // updated metadata.
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState
        {
            UserId = userId,
            CurrentFile = "s.mp3",
            FirstPlay = false,
            IsPlaying = true,
            NextFiles = new List<string>(),
        };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);

        var pending = Substitute.For<IPendingEditRepository>();
        pending.GetAsync(userId, "s.mp3", Arg.Any<CancellationToken>())
            .Returns(new PendingMetadataEdit
            {
                UserId = userId, File = "s.mp3", Title = "New Title",
                CreatedUtc = DateTime.UtcNow,
            });
        var writer = Substitute.For<IMetadataWriter>();
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(userId, "s.mp3").Returns(true);
        songs.GetByFileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dtos.SongDto("s.mp3", "Artist", "New Title", "Album", "2026", 200, null, null));
        songs.GetManyAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Dtos.SongDto>());
        var applier = new PendingMetadataApplier(
            pending, writer, storage,
            Substitute.For<ISongRecordRepository>(), songs,
            Substitute.For<IPodcastRecordRepository>(), Substitute.For<IPodcastReadModel>(),
            Substitute.For<ILogger<PendingMetadataApplier>>());

        var dto = await new NextHandler(playback, songs, applier, hub)
            .Handle(new NextCommand(userId), CancellationToken.None);

        dto.IsPlaying.Should().BeFalse();
        dto.CurrentSong.Should().NotBeNull();
        dto.CurrentSong!.File.Should().Be("s.mp3");
        dto.CurrentSong!.Title.Should().Be("New Title");
        await writer.Received(1).WriteSongAsync(
            userId, "s.mp3", "New Title", null, null, null, null, null,
            Arg.Any<CancellationToken>());
        await pending.Received(1).DeleteAsync(userId, "s.mp3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StateDto_ClampsRunawayPositionToTrackDuration()
    {
        // Wall-clock estimate outliving the track (stalled client element)
        // must not surface past the track length, or clients yank playback
        // to the duration edge and auto-advance mid-song.
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState
        {
            UserId = userId,
            CurrentFile = "b.mp3",
            FirstPlay = false,
            IsPlaying = true,
            PositionOffsetSec = 0,
            LastPlayUtc = DateTime.UnixEpoch,
            NextFiles = new List<string> { "c.mp3" },
        };
        playback.GetOrCreateAsync(userId, Arg.Any<CancellationToken>()).Returns(state);
        SongsResolve(songs, "b.mp3", 200, out _);

        var dto = await new NextHandler(playback, songs, NoOpApplier(), hub)
            .Handle(new NextCommand(userId, ExpectedFile: "a.mp3"), CancellationToken.None);

        dto.PositionSec.Should().Be(200);
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

public sealed class ReplaceQueueBroadcastTests
{
    // Replace never changes the current track (a play/next always follows),
    // so broadcasting its intermediate state echoes the OLD song into every
    // subscriber — UI flicker plus an engine reload with autoplay — right
    // before the real change lands. Queue only; the REST DTO stays full.
    [Fact]
    public async Task ReplaceQueue_BroadcastsQueueOnly_NotState()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState { UserId = userId, CurrentFile = "old.mp3", IsPlaying = true };
        playback.GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(state);
        songs.GetManyAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Dtos.SongDto>());
        songs.GetByFileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Dtos.SongDto?)null);

        var dto = await new QueueHandler(playback, songs, hub).Handle(
            new ReplaceQueueCommand(userId, "n1", ["n1", "n2"], null, null), CancellationToken.None);

        // REST response stays full (the awaiting playSong needs it)...
        dto.Should().NotBeNull();
        state.NextFiles.Should().Equal("n2");
        // ...but subscribers only get the queue, never the intermediate state.
        await hub.DidNotReceive().BroadcastStateAsync(
            Arg.Any<Guid>(), Arg.Any<Dtos.PlayerStateDto>(), Arg.Any<CancellationToken>());
        await hub.Received(1).BroadcastQueueAsync(
            userId, Arg.Any<IReadOnlyList<Dtos.SongDto>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherQueueOps_StillBroadcastState()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var songs = Substitute.For<ISongReadModel>();
        var hub = Substitute.For<IPlaybackHub>();
        var userId = Guid.NewGuid();
        var state = new PlaybackState { UserId = userId, CurrentFile = "c.mp3" };
        state.NextFiles = ["q1", "q2"];
        playback.GetOrCreateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(state);
        songs.GetManyAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Dtos.SongDto>());
        songs.GetByFileAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Dtos.SongDto?)null);

        await new QueueHandler(playback, songs, hub).Handle(
            new ReorderQueueCommand(userId, 0, 1), CancellationToken.None);

        await hub.Received(1).BroadcastStateAsync(
            userId, Arg.Any<Dtos.PlayerStateDto>(), Arg.Any<CancellationToken>());
    }
}
