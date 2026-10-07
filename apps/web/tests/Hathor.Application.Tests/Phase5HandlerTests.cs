using FluentAssertions;
using Hathor.Application.Podcasts;
using Hathor.Application.Settings;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Auth;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class Phase5HandlerTests : IAsyncLifetime
{
    private HathorDbContext _db = null!;
    private string _database = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) = await TestPostgres.CreateDatabaseAsync("hathor_phase5");
        _database = database;
        _db = new HathorDbContext(TestPostgres.Options(connectionString));
        InfrastructureServiceExtensions.EnsureDatabaseCreated(_db);
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        await TestPostgres.DropDatabaseAsync(_database);
    }

    [Fact]
    public async Task UpdateSettings_ClampsOutOfRange()
    {
        var handler = new SettingsHandlers(new EfUserSettingsRepository(_db));
        var dto = await handler.Handle(new UpdateSettingsCommand(
            _userId, Volume: 5, LimitDownloads: 99, null, 99, null, null, ChapterSkip: true),
            CancellationToken.None);
        dto.Volume.Should().Be(1);
        dto.LimitDownloads.Should().Be(20);
        dto.CrossfadeSeconds.Should().Be(12);
        dto.ChapterSkip.Should().BeTrue();
    }

    [Fact]
    public async Task SetPlayerSettings_ClampsSeconds()
    {
        var handler = new PlayerSettingsHandlers(new EfUserSettingsRepository(_db));
        var dto = await handler.Handle(
            new SetPlayerSettingsCommand(_userId, true, 60), CancellationToken.None);
        dto.CrossfadeEnabled.Should().BeTrue();
        dto.CrossfadeSeconds.Should().Be(12);
    }

    [Fact]
    public async Task DeletePodcast_RemovesTagLinks_RecordsTombstone()
    {
        var records = new EfPodcastRecordRepository(_db);
        await records.EnsureAsync(_userId, "e.mp3", "Ep", "Host", null);
        await records.SaveChangesAsync();
        var tag = new PodcastTag { UserId = _userId, Name = "Tech" };
        _db.PodcastTags.Add(tag);
        await _db.SaveChangesAsync();
        _db.PodcastTagLinks.Add(new PodcastTagLink
            { UserId = _userId, PodcastFile = "e.mp3", TagId = tag.Id });
        await _db.SaveChangesAsync();

        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var handler = new DeletePodcastHandler(
            storage, records, new EfLyricsRepository(_db),
            new EfTombstoneRepository(_db),
            new EfPlaybackStateRepository(_db),
            NSubstitute.Substitute.For<Ports.IPlaybackHub>(),
            NSubstitute.Substitute.For<Ports.ISongReadModel>());
        (await handler.Handle(new DeletePodcastCommand(_userId, "e.mp3"), CancellationToken.None))
            .Should().BeTrue();

        _db.Podcasts.Should().BeEmpty();
        _db.PodcastTagLinks.Should().BeEmpty();
        _db.SyncDeletions.Should().Contain(d =>
            d.TableName == "podcasts" && d.RowKey == "e.mp3");
        // Episode lyrics rows (previously leaked) are cleaned + tombstoned.
        _db.SyncDeletions.Should().Contain(d =>
            d.TableName == "lyrics" && d.RowKey == "e.mp3");
        // Tag itself survives (desktop keeps tags on episode delete).
        _db.PodcastTags.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdatePodcastMetadata_WritesTags_UpsertsRecord()
    {
        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        storage.PodcastPath(Arg.Any<Guid>(), Arg.Any<string>()).Returns(x => $"/tmp/{x.Arg<string>()}");
        var writer = NSubstitute.Substitute.For<Ports.IMetadataWriter>();
        var readModel = NSubstitute.Substitute.For<Ports.IPodcastReadModel>();
        readModel.GetByFileAsync(_userId, "e.mp3", Arg.Any<CancellationToken>())
            .Returns(new Dtos.SongDto("e.mp3", "Host", "New Title", "", "", 60, null, null, true));
        var records = new EfPodcastRecordRepository(_db);
        var playback = NSubstitute.Substitute.For<Domain.Repositories.IPlaybackStateRepository>();
        playback.GetOrCreateAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new Domain.Playback.PlaybackState { UserId = _userId });
        var pending = NSubstitute.Substitute.For<Domain.Repositories.IPendingEditRepository>();

        var handler = new UpdatePodcastMetadataHandler(storage, writer, records, readModel, playback, pending);
        var updated = await handler.Handle(
            new UpdatePodcastMetadataCommand(_userId, "e.mp3", "New Title", "Host", null),
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Pending.Should().BeFalse();
        updated!.Song.Title.Should().Be("New Title");
        await writer.Received(1).WritePathAsync(
            Arg.Any<string>(), "New Title", "Host",
            Arg.Is<string?>(x => x == null), Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null), Arg.Is<string?>(x => x == null),
            Arg.Any<CancellationToken>());
        _db.Podcasts.Should().ContainSingle(p => p.File == "e.mp3" && p.Title == "New Title");
    }

    [Fact]
    public async Task DeletePodcast_UnknownFile_ReturnsFalse()
    {        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var handler = new DeletePodcastHandler(
            storage, records: new EfPodcastRecordRepository(_db), new EfLyricsRepository(_db),
            new EfTombstoneRepository(_db),
            new EfPlaybackStateRepository(_db),
            NSubstitute.Substitute.For<Ports.IPlaybackHub>(),
            NSubstitute.Substitute.For<Ports.ISongReadModel>());
        (await handler.Handle(new DeletePodcastCommand(_userId, "nope.mp3"), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task PodcastTimestamps_FullCycle_OrderedByStart()
    {
        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        var repo = new EfPodcastTimestampRepository(_db);
        var create = new PodcastTimestamps.CreatePodcastTimestampHandler(storage, repo);
        var list = new PodcastTimestamps.ListPodcastTimestampsHandler(storage, repo);

        var second = await create.Handle(
            new PodcastTimestamps.CreatePodcastTimestampCommand(_userId, "e.mp3", "Second", 120, null),
            CancellationToken.None);
        second.Should().BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Created>();
        var first = await create.Handle(
            new PodcastTimestamps.CreatePodcastTimestampCommand(_userId, "e.mp3", "Intro", 0, 60),
            CancellationToken.None);
        var firstCreated = first.Should()
            .BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Created>().Subject;
        firstCreated.Timestamp.Id.Should().BeGreaterThan(0);
        var firstId = firstCreated.Timestamp.Id;

        var rows = (await list.Handle(
            new PodcastTimestamps.ListPodcastTimestampsQuery(_userId, "e.mp3"),
            CancellationToken.None))!;
        rows.Select(r => r.Name).Should().Equal("Intro", "Second");
        rows.First().EndSecs.Should().Be(60);

        var update = new PodcastTimestamps.UpdatePodcastTimestampHandler(storage, repo);
        var updated = await update.Handle(
            new PodcastTimestamps.UpdatePodcastTimestampCommand(
                _userId, "e.mp3", firstId, "Cold open", 5, null),
            CancellationToken.None);
        updated.Should().BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Updated>();

        var missing = await update.Handle(
            new PodcastTimestamps.UpdatePodcastTimestampCommand(
                _userId, "e.mp3", 999999, "Ghost", 5, null),
            CancellationToken.None);
        missing.Should().BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.TimestampNotFound>();

        var delete = new PodcastTimestamps.DeletePodcastTimestampHandler(storage, repo);
        (await delete.Handle(
            new PodcastTimestamps.DeletePodcastTimestampCommand(_userId, "e.mp3", firstId),
            CancellationToken.None)).Should().BeTrue();
        _db.PodcastTimestamps.Should().ContainSingle();
    }

    [Fact]
    public async Task PodcastTimestamps_Create_RejectsBadInput()
    {
        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        var handler = new PodcastTimestamps.CreatePodcastTimestampHandler(
            storage, new EfPodcastTimestampRepository(_db));
        async Task<PodcastTimestamps.PodcastTimestampWriteResult> Create(
            string name, double start, double? end) =>
            await handler.Handle(
                new PodcastTimestamps.CreatePodcastTimestampCommand(_userId, "e.mp3", name, start, end),
                CancellationToken.None);
        (await Create("", 10, null)).Should()
            .BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Invalid>();
        (await Create("   ", 10, null)).Should()
            .BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Invalid>();
        (await Create("Ok", -1, null)).Should()
            .BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Invalid>();
        (await Create("Ok", 30, 30)).Should()
            .BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Invalid>();
        (await Create("Ok", 30, 10)).Should()
            .BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.Invalid>();
        _db.PodcastTimestamps.Should().BeEmpty();
    }

    [Fact]
    public async Task PodcastTimestamps_MissingEpisode_ReturnsNotFound()
    {
        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var repo = new EfPodcastTimestampRepository(_db);
        var list = new PodcastTimestamps.ListPodcastTimestampsHandler(storage, repo);
        (await list.Handle(
            new PodcastTimestamps.ListPodcastTimestampsQuery(_userId, "gone.mp3"),
            CancellationToken.None)).Should().BeNull();
        var create = new PodcastTimestamps.CreatePodcastTimestampHandler(storage, repo);
        (await create.Handle(
            new PodcastTimestamps.CreatePodcastTimestampCommand(_userId, "gone.mp3", "X", 1, null),
            CancellationToken.None))
            .Should().BeOfType<PodcastTimestamps.PodcastTimestampWriteResult.EpisodeNotFound>();
    }

    [Fact]
    public async Task DeletePodcast_RemovesTimestamps()
    {
        var records = new EfPodcastRecordRepository(_db);
        await records.EnsureAsync(_userId, "e.mp3", "Ep", "Host", null);
        await records.SaveChangesAsync();
        _db.PodcastTimestamps.Add(new PodcastTimestamp
        { UserId = _userId, PodcastFile = "e.mp3", Name = "Intro", StartSecs = 0 });
        await _db.SaveChangesAsync();

        var storage = NSubstitute.Substitute.For<Ports.ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        var handler = new DeletePodcastHandler(
            storage, records, new EfLyricsRepository(_db),
            new EfTombstoneRepository(_db),
            new EfPlaybackStateRepository(_db),
            NSubstitute.Substitute.For<Ports.IPlaybackHub>(),
            NSubstitute.Substitute.For<Ports.ISongReadModel>());
        (await handler.Handle(new DeletePodcastCommand(_userId, "e.mp3"), CancellationToken.None))
            .Should().BeTrue();
        _db.PodcastTimestamps.Should().BeEmpty();
    }
}
