using FluentAssertions;
using Hathor.Application.Dtos;
using Hathor.Application.Metadata;
using Hathor.Application.Player;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

// PlayHandler/NextHandler raise the static PlayerEvents.SongPlayed, which
// PlayHandlerTests observes globally: run serially with it so raises here
// never land in its subscription window (and vice versa).
[CollectionDefinition("PlayerEventsSerial", DisableParallelization = true)]
public sealed class PlayerEventsSerialCollection { }

// Track changes must persist the NEW current file: the deferred-metadata
// hook used to load playback state a second time in the same scope, and the
// stale copy overwrote the mutated one at SaveChanges — every new song
// snapped back to the previous one in the database (while the response and
// history named the new one). Real scoped repository over InMemory, like
// production's shared scope (substitutes can't exhibit this: separate
// instances never share the tracked set).
[Collection("PlayerEventsSerial")]
public sealed class TrackChangePersistenceTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static HathorDbContext Db() =>
        new(new DbContextOptionsBuilder<HathorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SongDto Song(string file) =>
        new(file, "Artist", "Title", "Album", "2020", 180, null, null, false);

    private static ISongReadModel Songs()
    {
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(UserId, Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Song(call.ArgAt<string>(1)));
        songs.GetManyAsync(UserId, Arg.Any<IEnumerable<string>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SongDto>());
        return songs;
    }

    [Fact]
    public async Task Play_A_Then_B_PersistsB_AsCurrent()
    {
        await using var db = Db();
        // One scoped repository shared by both handler calls, as in prod.
        var playback = new EfPlaybackStateRepository(db);
        var applier = new PendingMetadataApplier(
            Substitute.For<IPendingEditRepository>(),
            Substitute.For<IMetadataWriter>(),
            Substitute.For<ILibraryStorage>(),
            Substitute.For<ISongRecordRepository>(),
            Songs(),
            Substitute.For<IPodcastRecordRepository>(),
            Substitute.For<IPodcastReadModel>(),
            Substitute.For<ILogger<PendingMetadataApplier>>());
        var hub = Substitute.For<IPlaybackHub>();
        PlayerEvents.Reset();

        await new PlayHandler(playback, Songs(), applier, hub)
            .Handle(new PlayCommand(UserId, "a.mp3", false, null), CancellationToken.None);
        await new PlayHandler(playback, Songs(), applier, hub)
            .Handle(new PlayCommand(UserId, "b.mp3", false, null), CancellationToken.None);

        var row = await db.PlaybackStates.FindAsync([UserId]);
        row.Should().NotBeNull();
        row!.CurrentFile.Should().Be("b.mp3");
        row.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public async Task DoubleLoadInOneScope_LastMutation_Wins()
    {
        // Pins the repository contract the deferred-metadata hook relies on:
        // two loads in one scope must resolve to ONE tracked copy, or the
        // second (stale) copy overwrites the first mutation at SaveChanges.
        await using var db = Db();
        var playback = new EfPlaybackStateRepository(db);

        var first = await playback.GetOrCreateAsync(UserId, CancellationToken.None);
        first.CurrentFile = "b.mp3";
        var second = await playback.GetOrCreateAsync(UserId, CancellationToken.None);
        second.Should().BeSameAs(first);
        await playback.SaveChangesAsync(CancellationToken.None);

        var row = await db.PlaybackStates.FindAsync([UserId]);
        row.Should().NotBeNull();
        row!.CurrentFile.Should().Be("b.mp3");
    }

    [Fact]
    public async Task Next_Advances_And_PersistsNewCurrent()
    {
        await using var db = Db();
        var playback = new EfPlaybackStateRepository(db);
        var applier = new PendingMetadataApplier(
            Substitute.For<IPendingEditRepository>(),
            Substitute.For<IMetadataWriter>(),
            Substitute.For<ILibraryStorage>(),
            Substitute.For<ISongRecordRepository>(),
            Songs(),
            Substitute.For<IPodcastRecordRepository>(),
            Substitute.For<IPodcastReadModel>(),
            Substitute.For<ILogger<PendingMetadataApplier>>());
        var hub = Substitute.For<IPlaybackHub>();
        PlayerEvents.Reset();

        var songs = Songs();
        await new PlayHandler(playback, songs, applier, hub)
            .Handle(new PlayCommand(UserId, "a.mp3", false, null), CancellationToken.None);
        await new QueueHandler(playback, songs, hub)
            .Handle(new ReplaceQueueCommand(UserId, "a.mp3", ["a.mp3", "b.mp3"], null, null), CancellationToken.None);
        await new NextHandler(playback, songs, applier, hub)
            .Handle(new NextCommand(UserId), CancellationToken.None);

        var row = await db.PlaybackStates.FindAsync([UserId]);
        row.Should().NotBeNull();
        row!.CurrentFile.Should().Be("b.mp3");
    }
}
