using FluentAssertions;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Application.Tests;

// History imports must be idempotent by natural key but still preserve a
// second device's play when its local autoincrement id collides.
public sealed class SyncHistoryImportTests : IAsyncLifetime
{
    private HathorDbContext _db = null!;
    private string _database = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) =
            await TestPostgres.CreateDatabaseAsync("hathor_synchistory");
        _database = database;
        _db = new HathorDbContext(TestPostgres.Options(connectionString));
        Hathor.Infrastructure.Auth.InfrastructureServiceExtensions.EnsureDatabaseCreated(_db);
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        await TestPostgres.DropDatabaseAsync(_database);
    }

    private static SyncSnapshot Snapshot(params MusicHistoryRowDto[] rows) =>
        new(
            Songs: null, Podcasts: null, Playlists: null, SongLinks: null,
            PodcastTags: null, PodcastTagLinks: null, Lyrics: null,
            MusicHistory: rows.ToList(), PlaylistHistory: null,
            DailyMix: null, Deletions: null);

    [Fact]
    public async Task Import_RekeysSameUserHistoryIdCollision()
    {
        var at = DateTime.UtcNow.AddHours(-2);
        _db.MusicHistory.Add(new MusicHistoryEntry
        {
            UserId = _userId,
            SongFile = "a.mp3",
            DatePlayedUtc = at,
        });
        await _db.SaveChangesAsync();
        var existingId = (await _db.MusicHistory.SingleAsync()).Id;

        var result = await new EfSyncService(_db).ImportAsync(
            _userId, Snapshot(new MusicHistoryRowDto(existingId, "b.mp3", at.AddSeconds(5))));

        result.Summary.MusicHistory.Should().Be(1);
        var rows = await _db.MusicHistory.AsNoTracking()
            .Where(h => h.UserId == _userId).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(h => h.SongFile == "b.mp3");
        rows.Should().ContainSingle(h => h.SongFile == "a.mp3");
    }

    [Fact]
    public async Task Import_DedupesNaturalKeyWithDifferentClientId()
    {
        var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _db.MusicHistory.Add(new MusicHistoryEntry
        {
            UserId = _userId,
            SongFile = "a.mp3",
            DatePlayedUtc = at,
        });
        await _db.SaveChangesAsync();

        var result = await new EfSyncService(_db).ImportAsync(
            _userId,
            Snapshot(new MusicHistoryRowDto(999_999, "a.mp3", at.AddMilliseconds(400))));

        result.Summary.MusicHistory.Should().Be(0);
        (await _db.MusicHistory.CountAsync(h => h.UserId == _userId)).Should().Be(1);
    }

    [Fact]
    public async Task Import_RetryIsIdempotent()
    {
        var at = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);
        var snapshot = Snapshot(new MusicHistoryRowDto(777, "a.mp3", at));

        var first = await new EfSyncService(_db).ImportAsync(_userId, snapshot);
        var second = await new EfSyncService(_db).ImportAsync(_userId, snapshot);

        first.Summary.MusicHistory.Should().Be(1);
        second.Summary.MusicHistory.Should().Be(0);
        (await _db.MusicHistory.CountAsync(h => h.UserId == _userId)).Should().Be(1);
    }
}
