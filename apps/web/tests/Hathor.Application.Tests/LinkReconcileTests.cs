using FluentAssertions;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Application.Tests;

// Pull link reconciliation (EF InMemory — runs without Postgres): local link
// rows absent from a full remote snapshot are dropped (unlinks + cascaded
// parent deletes); partial snapshots and /sync/import never wipe links.
public sealed class LinkReconcileTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static HathorDbContext Db() =>
        new(new DbContextOptionsBuilder<HathorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SyncSnapshot Links(
        List<SongLinkRowDto>? songLinks = null, List<TagLinkRowDto>? tagLinks = null) => new(
        Songs: null, Podcasts: null, Playlists: null, SongLinks: songLinks,
        PodcastTags: null, PodcastTagLinks: tagLinks, Lyrics: null, MusicHistory: null,
        PlaylistHistory: null, DailyMix: null, Deletions: null);

    private static async Task SeedSongLinks(HathorDbContext db)
    {
        db.SongPlaylists.Add(new SongPlaylist
            { Id = 1, UserId = UserId, SongFile = "keep.mp3", PlaylistId = 7, DateAddedUtc = DateTime.UtcNow });
        db.SongPlaylists.Add(new SongPlaylist
            { Id = 2, UserId = UserId, SongFile = "orphan.mp3", PlaylistId = 7, DateAddedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Reconcile_DropsOrphan_KeepsPresent()
    {
        await using var db = Db();
        await SeedSongLinks(db);

        await new EfSyncService(db).ImportAsync(UserId,
            Links(songLinks: [new SongLinkRowDto(1, "keep.mp3", 7, DateTime.UtcNow)]),
            CancellationToken.None, reconcileLinks: true);
        await db.SaveChangesAsync();

        (await db.SongPlaylists.Select(l => l.SongFile).ToListAsync())
            .Should().BeEquivalentTo("keep.mp3");
    }

    [Fact]
    public async Task NoReconcile_KeepsOrphan()
    {
        await using var db = Db();
        await SeedSongLinks(db);

        await new EfSyncService(db).ImportAsync(UserId,
            Links(songLinks: [new SongLinkRowDto(1, "keep.mp3", 7, DateTime.UtcNow)]),
            CancellationToken.None);
        await db.SaveChangesAsync();

        (await db.SongPlaylists.Select(l => l.SongFile).ToListAsync())
            .Should().BeEquivalentTo("keep.mp3", "orphan.mp3");
    }

    [Fact]
    public async Task Reconcile_EmptyRemoteSet_KeepsLocal()
    {
        // A fresh/failed remote must never wipe links.
        await using var db = Db();
        await SeedSongLinks(db);

        await new EfSyncService(db).ImportAsync(UserId,
            Links(songLinks: []), CancellationToken.None, reconcileLinks: true);
        await db.SaveChangesAsync();

        (await db.SongPlaylists.Select(l => l.SongFile).ToListAsync())
            .Should().BeEquivalentTo("keep.mp3", "orphan.mp3");
    }

    [Fact]
    public async Task Reconcile_DropsOrphanTagLinks()
    {
        await using var db = Db();
        db.PodcastTagLinks.Add(new PodcastTagLink
            { Id = 1, UserId = UserId, PodcastFile = "keep.mp3", TagId = 9 });
        db.PodcastTagLinks.Add(new PodcastTagLink
            { Id = 2, UserId = UserId, PodcastFile = "orphan.mp3", TagId = 9 });
        await db.SaveChangesAsync();

        await new EfSyncService(db).ImportAsync(UserId,
            Links(tagLinks: [new TagLinkRowDto(1, "keep.mp3", 9)]),
            CancellationToken.None, reconcileLinks: true);
        await db.SaveChangesAsync();

        (await db.PodcastTagLinks.Select(l => l.PodcastFile).ToListAsync())
            .Should().BeEquivalentTo("keep.mp3");
    }
}
