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
            _userId, Volume: 5, LimitDownloads: 99, null, 99, null, null),
            CancellationToken.None);
        dto.Volume.Should().Be(1);
        dto.LimitDownloads.Should().Be(20);
        dto.CrossfadeSeconds.Should().Be(12);
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
            storage, records,
            new EfTombstoneRepository(_db),
            new EfPlaybackStateRepository(_db),
            NSubstitute.Substitute.For<Ports.IPlaybackHub>(),
            NSubstitute.Substitute.For<Ports.ISongReadModel>());
        (await handler.Handle(new DeletePodcastCommand(_userId, "e.mp3"), CancellationToken.None))
            .Should().BeTrue();

        _db.Podcasts.Should().BeEmpty();
        _db.PodcastTagLinks.Should().BeEmpty();
        _db.SyncDeletions.Should().ContainSingle(d =>
            d.TableName == "podcasts" && d.RowKey == "e.mp3");
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

        var handler = new UpdatePodcastMetadataHandler(storage, writer, records, readModel);
        var updated = await handler.Handle(
            new UpdatePodcastMetadataCommand(_userId, "e.mp3", "New Title", "Host", null),
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Title.Should().Be("New Title");
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
            storage, records: new EfPodcastRecordRepository(_db),
            new EfTombstoneRepository(_db),
            new EfPlaybackStateRepository(_db),
            NSubstitute.Substitute.For<Ports.IPlaybackHub>(),
            NSubstitute.Substitute.For<Ports.ISongReadModel>());
        (await handler.Handle(new DeletePodcastCommand(_userId, "nope.mp3"), CancellationToken.None))
            .Should().BeFalse();
    }
}
