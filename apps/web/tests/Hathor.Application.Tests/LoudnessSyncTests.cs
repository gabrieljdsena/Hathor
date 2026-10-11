using FluentAssertions;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Application.Tests;

// Loudness sync mapping (EF InMemory — runs without Postgres): export
// carries measurements, import adopts them but never wipes a local one
// with a remote null (older remotes lack the column).
public sealed class LoudnessSyncTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static HathorDbContext Db() =>
        new(new DbContextOptionsBuilder<HathorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SyncSnapshot Snapshot(params SongRowDto[] songs) => new(
        Songs: songs.ToList(), Podcasts: null, Playlists: null, SongLinks: null,
        PodcastTags: null, PodcastTagLinks: null, Lyrics: null, MusicHistory: null,
        PlaylistHistory: null, DailyMix: null, Deletions: null);

    private static SongRowDto Row(string file, double? loudness) =>
        new(file, null, "Title", DateTime.UtcNow, "Artist", LoudnessDb: loudness);

    [Fact]
    public async Task Import_NewRow_KeepsRemoteLoudness()
    {
        await using var db = Db();
        await new EfSyncService(db).ImportAsync(UserId, Snapshot(Row("s.mp3", -9.5)), CancellationToken.None);
        await db.SaveChangesAsync();

        (await db.Songs.FirstAsync()).LoudnessDb.Should().Be(-9.5);
    }

    [Fact]
    public async Task Import_ExistingRow_AdoptsNonNull_NeverWipes()
    {
        await using var db = Db();
        db.Songs.Add(new Song
        {
            UserId = UserId, File = "a.mp3", Title = "A",
            DateDownloadUtc = DateTime.UtcNow, LoudnessDb = -8.0,
        });
        db.Songs.Add(new Song
        {
            UserId = UserId, File = "b.mp3", Title = "B",
            DateDownloadUtc = DateTime.UtcNow, LoudnessDb = null,
        });
        await db.SaveChangesAsync();

        await new EfSyncService(db).ImportAsync(UserId,
            Snapshot(Row("a.mp3", null), Row("b.mp3", -11.0)), CancellationToken.None);
        await db.SaveChangesAsync();

        (await db.Songs.FirstAsync(s => s.File == "a.mp3")).LoudnessDb.Should().Be(-8.0);
        (await db.Songs.FirstAsync(s => s.File == "b.mp3")).LoudnessDb.Should().Be(-11.0);
    }

    [Fact]
    public async Task Export_CarriesLoudness()
    {
        await using var db = Db();
        db.Songs.Add(new Song
        {
            UserId = UserId, File = "s.mp3", Title = "T",
            DateDownloadUtc = DateTime.UtcNow, LoudnessDb = -7.25,
        });
        await db.SaveChangesAsync();

        var snapshot = await new EfSyncService(db).ExportAsync(UserId, 0, CancellationToken.None);

        snapshot.Songs.Should().ContainSingle().Which.LoudnessDb.Should().Be(-7.25);
    }
}
