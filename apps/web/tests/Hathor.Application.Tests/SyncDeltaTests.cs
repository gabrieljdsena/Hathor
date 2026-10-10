using FluentAssertions;
using Hathor.Application.Ports;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Application.Tests;

// Delta sync over the Web API (replaces the remote-DB leg): empty cursor
// returns everything, the returned cursor makes the next call a no-op,
// later writes show up incrementally, malformed cursors bootstrap, import
// stamps UpdatedAtUtc and names server-missing files. EF InMemory.
public sealed class SyncDeltaTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static HathorDbContext Db() =>
        new(new DbContextOptionsBuilder<HathorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeStorage(HashSet<string> songs) : ILibraryStorage
    {
        public string SongsDir(Guid u) => "";
        public string PodcastsDir(Guid u) => "";
        public IReadOnlyList<string> ListSongFiles(Guid u) => songs.ToList();
        public string SongPath(Guid u, string f) => f;
        public bool SongExists(Guid u, string f) => songs.Contains(f);
        public IReadOnlyList<string> ListPodcastFiles(Guid u) => [];
        public string PodcastPath(Guid u, string f) => f;
        public bool PodcastExists(Guid u, string f) => false;
        public string? FindBackground(Guid u) => null;
        public Task<string> SaveBackgroundAsync(Guid u, byte[] b, string c, CancellationToken ct = default) =>
            Task.FromResult("");
        public Task DeleteBackgroundAsync(Guid u, CancellationToken ct = default) => Task.CompletedTask;
        public string BackgroundPath(Guid u, string f) => f;
    }

    private static async Task SeedAsync(HathorDbContext db)
    {
        db.Songs.Add(new Song
            { UserId = UserId, File = "a.mp3", Title = "A", DateDownloadUtc = DateTime.UtcNow });
        db.Playlists.Add(new Playlist { Id = 1, UserId = UserId, Title = "P" });
        db.MusicHistory.Add(new MusicHistoryEntry
            { Id = 1, UserId = UserId, SongFile = "a.mp3", DatePlayedUtc = DateTime.UtcNow });
        db.SyncDeletions.Add(new SyncDeletion
            { Id = 1, UserId = UserId, TableName = "songs", RowKey = "gone.mp3", DeletedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Delta_EmptyCursor_ReturnsEverything()
    {
        await using var db = Db();
        await SeedAsync(db);

        var delta = await new EfSyncService(db).GetDeltaAsync(UserId, "", CancellationToken.None);

        delta.Snapshot.Songs.Should().ContainSingle(s => s.File == "a.mp3");
        delta.Snapshot.Playlists.Should().ContainSingle(p => p.Id == 1);
        delta.Snapshot.MusicHistory.Should().ContainSingle(h => h.Id == 1);
        delta.Snapshot.Deletions.Should().ContainSingle(d => d.RowKey == "gone.mp3");
        delta.Cursor.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Delta_SecondCall_RepeatsWatermarkBatch()
    {
        // >= overlap is by design (same-tick writes must never be missed);
        // clients upsert idempotently, so the repeat is free.
        await using var db = Db();
        await SeedAsync(db);
        var svc = new EfSyncService(db);

        var first = await svc.GetDeltaAsync(UserId, "", CancellationToken.None);
        var second = await svc.GetDeltaAsync(UserId, first.Cursor, CancellationToken.None);

        second.Snapshot.Songs.Should().ContainSingle(s => s.File == "a.mp3");
        // History is id-cursored (exact): no repeat, unlike time sections.
        second.Snapshot.MusicHistory.Should().BeNull();
        second.Snapshot.PlaylistHistory.Should().BeNull();
        second.Cursor.Should().Be(first.Cursor);
    }

    [Fact]
    public async Task Delta_PicksUpLaterWrite()
    {
        await using var db = Db();
        await SeedAsync(db);
        var svc = new EfSyncService(db);
        var cursor = (await svc.GetDeltaAsync(UserId, "", CancellationToken.None)).Cursor;

        await Task.Delay(50); // watermark resolution: a later write must sort after it
        db.Songs.Add(new Song
            { UserId = UserId, File = "b.mp3", Title = "B", DateDownloadUtc = DateTime.UtcNow });
        db.MusicHistory.Add(new MusicHistoryEntry
            { Id = 2, UserId = UserId, SongFile = "b.mp3", DatePlayedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var delta = await svc.GetDeltaAsync(UserId, cursor, CancellationToken.None);

        delta.Snapshot.Songs.Should().Contain(s => s.File == "b.mp3");
        delta.Snapshot.MusicHistory.Should().ContainSingle(h => h.Id == 2);
        delta.Cursor.Should().NotBe(cursor);
    }

    [Fact]
    public async Task Delta_MalformedCursor_Bootstraps()
    {
        await using var db = Db();
        await SeedAsync(db);

        var delta = await new EfSyncService(db).GetDeltaAsync(UserId, "junk", CancellationToken.None);

        delta.Snapshot.Songs.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_StampsUpdatedAt_And_ReportsMissingFiles()
    {
        await using var db = Db();
        var svc = new EfSyncService(db, new FakeStorage([]));

        var result = await svc.ImportAsync(UserId, new SyncSnapshot(
            Songs: [new SongRowDto("new.mp3", null, "New", DateTime.UtcNow, "Somebody")],
            Podcasts: null, Playlists: null, SongLinks: null, PodcastTags: null,
            PodcastTagLinks: null, Lyrics: null, MusicHistory: null,
            PlaylistHistory: null, DailyMix: null, Deletions: null),
            CancellationToken.None);

        result.Summary.Songs.Should().Be(1);
        result.MissingFiles.Should().BeEquivalentTo("new.mp3");
        (await db.Songs.SingleAsync()).UpdatedAtUtc.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task Import_ExistingFile_NotMissing()
    {
        await using var db = Db();
        var svc = new EfSyncService(db, new FakeStorage(["have.mp3"]));

        var result = await svc.ImportAsync(UserId, new SyncSnapshot(
            Songs: [new SongRowDto("have.mp3", null, "Have", DateTime.UtcNow, null)],
            Podcasts: null, Playlists: null, SongLinks: null, PodcastTags: null,
            PodcastTagLinks: null, Lyrics: null, MusicHistory: null,
            PlaylistHistory: null, DailyMix: null, Deletions: null),
            CancellationToken.None);

        result.MissingFiles.Should().BeEmpty();
    }
}
