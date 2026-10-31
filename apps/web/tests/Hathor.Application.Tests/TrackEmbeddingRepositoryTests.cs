using FluentAssertions;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Repositories;

namespace Hathor.Application.Tests;

public sealed class TrackEmbeddingRepositoryTests : IAsyncLifetime
{
    private HathorDbContext _db = null!;
    private string _database = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) =
            await TestPostgres.CreateDatabaseAsync("hathor_embeddings");
        _database = database;
        _db = new HathorDbContext(TestPostgres.Options(connectionString));
        Hathor.Infrastructure.Auth.InfrastructureServiceExtensions.EnsureDatabaseCreated(_db);
        _db.Songs.Add(new Song
        {
            UserId = _userId,
            File = "song.mp3",
            Title = "Song",
            Artist = "Artist",
            DateDownloadUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        await TestPostgres.DropDatabaseAsync(_database);
    }

    [Fact]
    public async Task PopularityCache_UpsertsAndReads()
    {
        var repo = new EfTrackPopularityRepository(_db);

        await repo.UpsertAsync(new Dictionary<string, long> { ["mb-a"] = 100 });
        await repo.SaveChangesAsync();
        var first = await repo.GetManyAsync(["mb-a", "mb-b"]);
        first["mb-a"].Should().Be(100);
        first.Should().NotContainKey("mb-b");

        await repo.UpsertAsync(new Dictionary<string, long> { ["mb-a"] = 200 });
        await repo.SaveChangesAsync();
        (await repo.GetManyAsync(["mb-a"]))["mb-a"].Should().Be(200);
    }

    [Fact]
    public async Task MissingSongs_ExcludeEmbeddedFiles()
    {
        var repo = new EfTrackEmbeddingRepository(_db);

        (await repo.GetMissingSongFilesAsync(_userId, "clap-v1", 10))
            .Should().Equal("song.mp3");

        await repo.SaveAsync(new TrackEmbedding
        {
            UserId = _userId,
            OwnerType = "song",
            OwnerKey = "song.mp3",
            Model = "clap-v1",
            VectorJson = "[1,0]",
            Dimension = 2,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await repo.SaveChangesAsync();

        (await repo.GetMissingSongFilesAsync(_userId, "clap-v1", 10)).Should().BeEmpty();
        (await repo.GetAsync(_userId, "song", "song.mp3", "clap-v1")).Should().NotBeNull();
        (await repo.GetAllAsync(_userId, "song", "clap-v1")).Should().HaveCount(1);
        (await repo.GetManyAsync(_userId, "song", ["song.mp3"], "clap-v1"))
            .Should().HaveCount(1);
    }
}
