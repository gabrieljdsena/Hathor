using Hathor.Application.Ports;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Infrastructure.Sync;

// Snapshot merge (desktop DatabaseManager.sync_remote_to_local_and_download +
// DatabaseSync push, adapted): upsert everything present, insert-if-absent
// for append-only history, adopt daily mixes (prune < today), apply
// tombstones. Missing sections are skipped (guarded old-DB behavior).
public sealed class EfSyncService(HathorDbContext db) : ISyncService
{
    public async Task<SyncSnapshot> ExportAsync(Guid userId, long sinceId, CancellationToken ct = default) =>
        new(
            Songs: await db.Songs.Where(s => s.UserId == userId)
                .Select(s => new SongRowDto(s.File, s.DownloadedLink, s.Title, s.DateDownloadUtc, s.Artist, s.LoudnessDb))
                .ToListAsync(ct),
            Podcasts: await db.Podcasts.Where(p => p.UserId == userId)
                .Select(p => new PodcastRowDto(p.File, p.DownloadedLink, p.Title, p.DateDownloadUtc, p.Artist))
                .ToListAsync(ct),
            Playlists: await db.Playlists.Where(p => p.UserId == userId)
                .Select(p => new PlaylistRowDto(p.Id, p.Title, p.Description, p.Thumbnail))
                .ToListAsync(ct),
            SongLinks: await db.SongPlaylists.Where(l => l.UserId == userId)
                .Select(l => new SongLinkRowDto(l.Id, l.SongFile, l.PlaylistId, l.DateAddedUtc))
                .ToListAsync(ct),
            PodcastTags: await db.PodcastTags.Where(t => t.UserId == userId)
                .Select(t => new TagRowDto(t.Id, t.Name))
                .ToListAsync(ct),
            PodcastTagLinks: await db.PodcastTagLinks.Where(l => l.UserId == userId)
                .Select(l => new TagLinkRowDto(l.Id, l.PodcastFile, l.TagId))
                .ToListAsync(ct),
            Lyrics: await db.Lyrics.Where(l => l.UserId == userId)
                .Select(l => new LyricRowDto(l.Id, l.SongFile, l.LyricsJson, l.OffsetMs))
                .ToListAsync(ct),
            MusicHistory: await db.MusicHistory.Where(h => h.UserId == userId && h.Id > sinceId)
                .Select(h => new MusicHistoryRowDto(h.Id, h.SongFile, h.DatePlayedUtc))
                .ToListAsync(ct),
            PlaylistHistory: await db.PlaylistHistory.Where(h => h.UserId == userId && h.Id > sinceId)
                .Select(h => new PlaylistHistoryRowDto(h.Id, h.PlaylistId, h.DatePlayedUtc))
                .ToListAsync(ct),
            DailyMix: await db.DailyMixes.Where(m => m.UserId == userId)
                .Select(m => new MixRowDto(m.MixDate, m.SongFilesJson))
                .ToListAsync(ct),
            Deletions: await db.SyncDeletions.Where(d => d.UserId == userId)
                .Select(d => new DeletionRowDto(d.TableName, d.RowKey))
                .ToListAsync(ct));

    public async Task<SyncSummary> ImportAsync(Guid userId, SyncSnapshot s, CancellationToken ct = default)
    {
        var counts = new int[11];
        // Explicit snapshot ids come from per-device sequences and collide
        // across users (single-column PKs are one global namespace, like the
        // desktop remote DB). Colliding rows are re-keyed; FKs are remapped.
        var playlistRemap = new Dictionary<long, long>();
        var tagRemap = new Dictionary<long, long>();

        if (s.Songs is not null)
            foreach (var r in s.Songs)
            {
                var existing = await db.Songs.FindAsync([userId, r.File], ct);
                if (existing is null)
                {
                    await db.Songs.AddAsync(new Song
                    {
                        UserId = userId, File = r.File, DownloadedLink = r.DownloadedLink,
                        Title = r.Title, DateDownloadUtc = r.DateDownloadUtc, Artist = r.Artist,
                        LoudnessDb = r.LoudnessDb,
                    }, ct);
                    counts[0]++;
                }
                else
                {
                    existing.DownloadedLink = r.DownloadedLink;
                    existing.Title = r.Title;
                    existing.DateDownloadUtc = r.DateDownloadUtc;
                    existing.Artist = r.Artist;
                    // Adopt remote measurements, never wipe a local one
                    // with a remote null (older remotes lack the column).
                    if (r.LoudnessDb.HasValue) existing.LoudnessDb = r.LoudnessDb;
                }
            }

        if (s.Podcasts is not null)
            foreach (var r in s.Podcasts)
            {
                var existing = await db.Podcasts.FindAsync([userId, r.File], ct);
                if (existing is null)
                {
                    await db.Podcasts.AddAsync(new Podcast
                    {
                        UserId = userId, File = r.File, DownloadedLink = r.DownloadedLink,
                        Title = r.Title, DateDownloadUtc = r.DateDownloadUtc, Artist = r.Artist,
                    }, ct);
                    counts[1]++;
                }
                else
                {
                    existing.DownloadedLink = r.DownloadedLink;
                    existing.Title = r.Title;
                    existing.DateDownloadUtc = r.DateDownloadUtc;
                    existing.Artist = r.Artist;
                }
            }

        if (s.Playlists is not null)
            foreach (var r in s.Playlists)
            {
                var existing = await db.Playlists.FindAsync([r.Id], ct);
                if (existing is null)
                {
                    await db.Playlists.AddAsync(new Playlist
                    {
                        Id = r.Id, UserId = userId, Title = r.Title,
                        Description = r.Description, Thumbnail = r.Thumbnail,
                    }, ct);
                    counts[2]++;
                }
                else if (existing.UserId == userId)
                {
                    existing.Title = r.Title;
                    existing.Description = r.Description;
                    existing.Thumbnail = r.Thumbnail;
                }
                else
                {
                    // Another user's row owns this id — re-key a copy.
                    var clone = new Playlist
                    {
                        UserId = userId, Title = r.Title,
                        Description = r.Description, Thumbnail = r.Thumbnail,
                    };
                    await db.Playlists.AddAsync(clone, ct);
                    await db.SaveChangesAsync(ct);
                    playlistRemap[r.Id] = clone.Id;
                    counts[2]++;
                }
            }

        if (s.SongLinks is not null)
            foreach (var r in s.SongLinks)
            {
                var pid = playlistRemap.GetValueOrDefault(r.PlaylistId, r.PlaylistId);
                var existing = await db.SongPlaylists.FindAsync([r.Id], ct);
                if (existing is null)
                {
                    await db.SongPlaylists.AddAsync(new SongPlaylist
                    {
                        Id = r.Id, UserId = userId, SongFile = r.SongFile,
                        PlaylistId = pid, DateAddedUtc = r.DateAddedUtc,
                    }, ct);
                    counts[3]++;
                }
                else if (existing.UserId == userId)
                {
                    existing.SongFile = r.SongFile;
                    existing.PlaylistId = pid;
                    existing.DateAddedUtc = r.DateAddedUtc;
                }
                else
                {
                    await db.SongPlaylists.AddAsync(new SongPlaylist
                    {
                        UserId = userId, SongFile = r.SongFile,
                        PlaylistId = pid, DateAddedUtc = r.DateAddedUtc,
                    }, ct);
                    counts[3]++;
                }
            }

        if (s.PodcastTags is not null)
            foreach (var r in s.PodcastTags)
            {
                var existing = await db.PodcastTags.FindAsync([r.Id], ct);
                if (existing is null)
                {
                    await db.PodcastTags.AddAsync(new PodcastTag
                        { Id = r.Id, UserId = userId, Name = r.Name }, ct);
                    counts[4]++;
                }
                else if (existing.UserId == userId)
                    existing.Name = r.Name;
                else
                {
                    var clone = new PodcastTag { UserId = userId, Name = r.Name };
                    await db.PodcastTags.AddAsync(clone, ct);
                    await db.SaveChangesAsync(ct);
                    tagRemap[r.Id] = clone.Id;
                    counts[4]++;
                }
            }

        if (s.PodcastTagLinks is not null)
            foreach (var r in s.PodcastTagLinks)
            {
                var tid = tagRemap.GetValueOrDefault(r.TagId, r.TagId);
                var existing = await db.PodcastTagLinks.FindAsync([r.Id], ct);
                if (existing is null)
                {
                    await db.PodcastTagLinks.AddAsync(new PodcastTagLink
                    {
                        Id = r.Id, UserId = userId, PodcastFile = r.PodcastFile, TagId = tid,
                    }, ct);
                    counts[5]++;
                }
                else if (existing.UserId == userId)
                {
                    existing.PodcastFile = r.PodcastFile;
                    existing.TagId = tid;
                }
                else
                {
                    await db.PodcastTagLinks.AddAsync(new PodcastTagLink
                    {
                        UserId = userId, PodcastFile = r.PodcastFile, TagId = tid,
                    }, ct);
                    counts[5]++;
                }
            }

        if (s.Lyrics is not null)
            foreach (var r in s.Lyrics)
            {
                // Chapter cache rows from older pushes: never import them
                // (see SyncedLyricsGuards — desktop never reads them either).
                if (Application.Lyrics.SyncedLyricsGuards.IsChapterLyricsRow(r.SongFile))
                    continue;
                var existing = await db.Lyrics.FindAsync([r.Id], ct);
                if (existing is null)
                {
                    await db.Lyrics.AddAsync(new Lyric
                    {
                        Id = r.Id, UserId = userId, SongFile = r.SongFile, LyricsJson = r.LyricsJson,
                        OffsetMs = r.OffsetMs,
                    }, ct);
                    counts[6]++;
                }
                else if (existing.UserId == userId)
                {
                    existing.SongFile = r.SongFile;
                    existing.LyricsJson = r.LyricsJson;
                    existing.OffsetMs = r.OffsetMs;
                }
                else
                {
                    await db.Lyrics.AddAsync(new Lyric
                    {
                        UserId = userId, SongFile = r.SongFile, LyricsJson = r.LyricsJson,
                        OffsetMs = r.OffsetMs,
                    }, ct);
                    counts[6]++;
                }
            }

        // Append-only history: insert-if-absent by id (desktop INSERT IGNORE),
        // re-keying on cross-user collisions.
        if (s.MusicHistory is not null)
            foreach (var r in s.MusicHistory)
                if (!await db.MusicHistory.AnyAsync(h => h.Id == r.Id, ct))
                {
                    await db.MusicHistory.AddAsync(new MusicHistoryEntry
                    {
                        Id = r.Id, UserId = userId, SongFile = r.SongFile, DatePlayedUtc = r.DatePlayedUtc,
                    }, ct);
                    counts[7]++;
                }
                else if (!await db.MusicHistory.AnyAsync(
                    h => h.Id == r.Id && h.UserId == userId, ct))
                {
                    await db.MusicHistory.AddAsync(new MusicHistoryEntry
                    {
                        UserId = userId, SongFile = r.SongFile, DatePlayedUtc = r.DatePlayedUtc,
                    }, ct);
                    counts[7]++;
                }

        if (s.PlaylistHistory is not null)
            foreach (var r in s.PlaylistHistory)
            {
                var pid = playlistRemap.GetValueOrDefault(r.PlaylistId, r.PlaylistId);
                if (!await db.PlaylistHistory.AnyAsync(h => h.Id == r.Id, ct))
                {
                    await db.PlaylistHistory.AddAsync(new PlaylistHistoryEntry
                    {
                        Id = r.Id, UserId = userId, PlaylistId = pid, DatePlayedUtc = r.DatePlayedUtc,
                    }, ct);
                    counts[8]++;
                }
                else if (!await db.PlaylistHistory.AnyAsync(
                    h => h.Id == r.Id && h.UserId == userId, ct))
                {
                    await db.PlaylistHistory.AddAsync(new PlaylistHistoryEntry
                    {
                        UserId = userId, PlaylistId = pid, DatePlayedUtc = r.DatePlayedUtc,
                    }, ct);
                    counts[8]++;
                }
            }

        // Adopt remote mixes; prune anything older than today.
        if (s.DailyMix is not null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
            foreach (var r in s.DailyMix)
            {
                var existing = await db.DailyMixes.FindAsync([userId, r.MixDate], ct);
                if (existing is null)
                {
                    await db.DailyMixes.AddAsync(new DailyMix
                    {
                        UserId = userId, MixDate = r.MixDate, SongFilesJson = r.SongFilesJson,
                        CreatedAtUtc = DateTime.UtcNow,
                    }, ct);
                    counts[9]++;
                }
                else
                    existing.SongFilesJson = r.SongFilesJson;
            }
            await db.DailyMixes
                .Where(m => m.UserId == userId && string.Compare(m.MixDate, today) < 0)
                .ExecuteDeleteAsync(ct);
        }

        if (s.Deletions is not null)
            foreach (var d in s.Deletions)
                if (await ApplyDeletionAsync(userId, d.TableName, d.RowKey, ct))
                    counts[10]++;

        await db.SaveChangesAsync(ct);
        await AlignAutoIncrementAsync(ct);

        return new SyncSummary(counts[0], counts[1], counts[2], counts[3], counts[4],
            counts[5], counts[6], counts[7], counts[8], counts[9], counts[10]);
    }

    // Desktop REMOTE_DELETE_COLUMNS mapping (lowercase web table names here).
    private async Task<bool> ApplyDeletionAsync(Guid userId, string table, string key, CancellationToken ct)
    {
        var deleted = table switch
        {
            "songs" => await db.Songs.Where(x => x.UserId == userId && x.File == key)
                .ExecuteDeleteAsync(ct),
            "podcasts" => await db.Podcasts.Where(x => x.UserId == userId && x.File == key)
                .ExecuteDeleteAsync(ct),
            "playlists" when long.TryParse(key, out var pid) => await db.Playlists
                .Where(x => x.UserId == userId && x.Id == pid).ExecuteDeleteAsync(ct),
            "lyrics" => await db.Lyrics.Where(x => x.UserId == userId && x.SongFile == key)
                .ExecuteDeleteAsync(ct),
            "music_history" => await db.MusicHistory.Where(x => x.UserId == userId && x.SongFile == key)
                .ExecuteDeleteAsync(ct),
            "playlist_history" when long.TryParse(key, out var hid) => await db.PlaylistHistory
                .Where(x => x.UserId == userId && x.PlaylistId == hid).ExecuteDeleteAsync(ct),
            "podcast_tags" when long.TryParse(key, out var tid) => await db.PodcastTags
                .Where(x => x.UserId == userId && x.Id == tid).ExecuteDeleteAsync(ct),
            _ => 0,
        };
        if (deleted > 0)
        {
            var dup = await db.SyncDeletions.AnyAsync(d =>
                d.UserId == userId && d.TableName == table && d.RowKey == key, ct);
            if (!dup)
                await db.SyncDeletions.AddAsync(new SyncDeletion
                {
                    UserId = userId, TableName = table, RowKey = key, DeletedAtUtc = DateTime.UtcNow,
                }, ct);
            return true;
        }
        return false;
    }

    // Keep AUTO_INCREMENT aligned after explicit-id inserts (desktop
    // _align_auto_increment; SQLite handles this natively — MySQL only).
    // Table/column names are hardcoded constants; only the long max varies.
#pragma warning disable EF1002
    private async Task AlignAutoIncrementAsync(CancellationToken ct)
    {
        if (!db.Database.IsMySql()) return;
        foreach (var (table, idCol) in new[]
            { ("Playlists", "Id"), ("Song_Playlist", "Id"), ("Podcast_Tags", "Id"),
              ("Podcast_Tag_Links", "Id"), ("Lyrics", "Id"), ("Music_History", "Id"),
              ("Playlist_History", "Id") })
        {
            var max = await db.Database
                .SqlQueryRaw<long>($"SELECT COALESCE(MAX(`{idCol}`), 0) FROM `{table}`")
                .SingleAsync(ct);
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE `{table}` AUTO_INCREMENT = {max + 1}", ct);
        }
    }
#pragma warning restore EF1002
}
