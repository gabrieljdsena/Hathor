using FluentAssertions;
using Hathor.Application.Metadata;
using Hathor.Application.Playlists;
using Hathor.Application.PodcastTags;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Auth;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class Phase2HandlerTests : IAsyncLifetime
{
    // Ephemeral Postgres database (real SQL incl. ExecuteDelete,
    // unlike the InMemory provider), migrated via EnsureDatabaseCreated
    // so the Postgres InitialCreate is covered on every run.
    private HathorDbContext _db = null!;
    private string _database = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) = await TestPostgres.CreateDatabaseAsync("hathor_phase2");
        _database = database;
        _db = new HathorDbContext(TestPostgres.Options(connectionString));
        InfrastructureServiceExtensions.EnsureDatabaseCreated(_db);
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        await TestPostgres.DropDatabaseAsync(_database);
    }

    private EfPlaylistRepository Playlists => new(_db);
    private EfPodcastTagRepository Tags => new(_db);
    private EfSongRecordRepository Records => new(_db);
    private EfTombstoneRepository Tombstones => new(_db);

    [Fact]
    public async Task SetSongPlaylists_ReplacesLinks_IgnoresUnknownIds_EnsuresSong()
    {
        var p1 = new Playlist { UserId = _userId, Title = "A" };
        var p2 = new Playlist { UserId = _userId, Title = "B" };
        _db.Playlists.AddRange(p1, p2);
        await _db.SaveChangesAsync();
        await Playlists.AddLinkAsync(_userId, "s.mp3", p1.Id, DateTime.UtcNow);
        await Playlists.SaveChangesAsync();

        var handler = new SetSongPlaylistsHandler(Playlists, Records);
        (await handler.Handle(new SetSongPlaylistsCommand(
            _userId, "s.mp3", "Song", [p2.Id, 9999]), CancellationToken.None)).Should().BeTrue();

        (await Playlists.GetIdsForSongAsync(_userId, "s.mp3"))
            .Should().BeEquivalentTo([p2.Id]);
        (await Records.GetAsync(_userId, "s.mp3")).Should().NotBeNull();
    }

    [Fact]
    public async Task CreatePodcastTag_Blank_ReturnsMinusOne_Duplicate_ReturnsExisting()
    {
        var handler = new CreatePodcastTagHandler(Tags);
        (await handler.Handle(new CreatePodcastTagCommand(_userId, "  "), CancellationToken.None))
            .Should().Be(-1);
        var id = await handler.Handle(new CreatePodcastTagCommand(_userId, "Tech"), CancellationToken.None);
        id.Should().BePositive();
        (await handler.Handle(new CreatePodcastTagCommand(_userId, "Tech"), CancellationToken.None))
            .Should().Be(id);
    }

    [Fact]
    public async Task RenamePodcastTag_BlankOrDuplicate_ReturnsFalse()
    {
        var create = new CreatePodcastTagHandler(Tags);
        var a = await create.Handle(new CreatePodcastTagCommand(_userId, "A"), CancellationToken.None);
        await create.Handle(new CreatePodcastTagCommand(_userId, "B"), CancellationToken.None);
        var rename = new RenamePodcastTagHandler(Tags);
        (await rename.Handle(new RenamePodcastTagCommand(_userId, a, "  "), CancellationToken.None))
            .Should().BeFalse();
        (await rename.Handle(new RenamePodcastTagCommand(_userId, a, "B"), CancellationToken.None))
            .Should().BeFalse();
        (await rename.Handle(new RenamePodcastTagCommand(_userId, a, "C"), CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task DeletePodcastTag_RemovesLinks_RecordsTombstone()
    {
        var create = new CreatePodcastTagHandler(Tags);
        var id = await create.Handle(new CreatePodcastTagCommand(_userId, "X"), CancellationToken.None);
        await Tags.AssignAsync(_userId, "e.mp3", id);
        await Tags.SaveChangesAsync();

        var handler = new DeletePodcastTagHandler(Tags, Tombstones);
        (await handler.Handle(new DeletePodcastTagCommand(_userId, id), CancellationToken.None))
            .Should().BeTrue();
        (await Tags.GetByIdAsync(_userId, id)).Should().BeNull();
        _db.SyncDeletions.Should().ContainSingle(d =>
            d.TableName == "podcast_tags" && d.RowKey == id.ToString());
    }

    [Fact]
    public async Task UpdateSongMetadata_PreservesGenre_ReturnsResumeSecWhenCurrent()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        var writer = Substitute.For<IMetadataWriter>();
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(_userId, "s.mp3", true, Arg.Any<CancellationToken>())
            .Returns(new Dtos.SongDto("s.mp3", "Artist", "Title", "Album", "2020", 180, null, null, false, "Rock"));
        var playback = Substitute.For<IPlaybackStateRepository>();
        var state = new PlaybackState { UserId = _userId, CurrentFile = "s.mp3", IsPlaying = true, FirstPlay = false };
        playback.GetOrCreateAsync(_userId, Arg.Any<CancellationToken>()).Returns(state);

        var handler = new UpdateSongMetadataHandler(storage, writer, Records, songs, playback);
        var result = await handler.Handle(new UpdateSongMetadataCommand(
            _userId, "s.mp3", "New Title", null, null, null, null, null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Song.Genre.Should().Be("Rock"); // null genre kept existing
        result.ResumeSec.Should().BeGreaterThanOrEqualTo(0);
        await writer.Received(1).WriteSongAsync(
            _userId, "s.mp3", "New Title", null, null, null, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSongMetadata_MissingFile_ReturnsNull()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var handler = new UpdateSongMetadataHandler(
            storage,
            Substitute.For<IMetadataWriter>(),
            Records,
            Substitute.For<ISongReadModel>(),
            Substitute.For<IPlaybackStateRepository>());
        (await handler.Handle(new UpdateSongMetadataCommand(
            _userId, "gone.mp3", "T", null, null, null, null, null), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task DeleteSong_Cascades_EvictsQueue_StopsIfCurrent_RecordsTombstones()
    {
        _db.Songs.Add(new Song { UserId = _userId, File = "s.mp3", Title = "S", DateDownloadUtc = DateTime.UtcNow });
        _db.MusicHistory.Add(new MusicHistoryEntry { UserId = _userId, SongFile = "s.mp3", DatePlayedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var playback = Substitute.For<IPlaybackStateRepository>();
        var state = new PlaybackState
        {
            UserId = _userId,
            CurrentFile = "s.mp3",
            IsPlaying = true,
            FirstPlay = false,
            NextFiles = ["s.mp3", "other.mp3"],
            PrevFiles = ["s.mp3"],
        };
        playback.GetOrCreateAsync(_userId, Arg.Any<CancellationToken>()).Returns(state);
        var hub = Substitute.For<IPlaybackHub>();
        var songs = Substitute.For<ISongReadModel>();

        var handler = new DeleteSongHandler(storage, Records, Tombstones, playback, hub, songs);
        (await handler.Handle(new DeleteSongCommand(_userId, "s.mp3"), CancellationToken.None))
            .Should().BeTrue();

        _db.Songs.Should().BeEmpty();
        _db.MusicHistory.Should().BeEmpty();
        _db.SyncDeletions.Select(d => d.TableName).Should()
            .BeEquivalentTo("songs", "lyrics", "music_history");
        state.IsPlaying.Should().BeFalse();
        state.FirstPlay.Should().BeTrue();
        state.NextFiles.Should().BeEquivalentTo("other.mp3");
        state.PrevFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePlaylist_ThumbnailRemove_SetsNull()
    {
        var playlists = Substitute.For<IPlaylistRepository>();
        var existing = new Playlist { Id = 7, UserId = _userId, Title = "P", Thumbnail = "data:old" };
        playlists.GetAsync(_userId, 7, Arg.Any<CancellationToken>()).Returns(existing);

        var dto = await new UpdatePlaylistHandler(playlists).Handle(
            new UpdatePlaylistCommand(_userId, 7, null, null, "REMOVE"), CancellationToken.None);

        dto!.Thumbnail.Should().BeNull();
        existing.Thumbnail.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePlaylist_ThumbnailNull_KeepsExisting()
    {
        var playlists = Substitute.For<IPlaylistRepository>();
        var existing = new Playlist { Id = 7, UserId = _userId, Title = "P", Thumbnail = "data:old" };
        playlists.GetAsync(_userId, 7, Arg.Any<CancellationToken>()).Returns(existing);

        var dto = await new UpdatePlaylistHandler(playlists).Handle(
            new UpdatePlaylistCommand(_userId, 7, "New", null, null), CancellationToken.None);

        dto!.Title.Should().Be("New");
        dto.Thumbnail.Should().Be("data:old");
    }

    [Fact]
    public async Task DeletePlaylist_RemovesLinksAndHistory_RecordsTombstones()
    {
        var p = new Playlist { UserId = _userId, Title = "P" };
        _db.Playlists.Add(p);
        await _db.SaveChangesAsync();
        await Playlists.AddLinkAsync(_userId, "s.mp3", p.Id, DateTime.UtcNow);
        _db.PlaylistHistory.Add(new PlaylistHistoryEntry
            { UserId = _userId, PlaylistId = p.Id, DatePlayedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var handler = new DeletePlaylistHandler(Playlists, Tombstones);
        (await handler.Handle(new DeletePlaylistCommand(_userId, p.Id), CancellationToken.None))
            .Should().BeTrue();
        _db.Playlists.Should().BeEmpty();
        _db.SongPlaylists.Should().BeEmpty();
        _db.PlaylistHistory.Should().BeEmpty();
        _db.SyncDeletions.Select(d => d.TableName).Should()
            .BeEquivalentTo("playlists", "playlist_history");
    }
}
