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
public sealed class EfSyncService(HathorDbContext db, ILibraryStorage? storage = null) : ISyncService
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

    // Opaque delta cursor: "<utcTicks>:<musicHistoryMaxId>:<playlistHistoryMaxId>".
    // Rows use >= against the time watermark (overlap is idempotent on
    // clients); history stays exact via id comparison.
    private static (DateTime Since, long MusicId, long PlaylistId) ParseCursor(string cursor)
    {
        var parts = (cursor ?? "").Split(':');
        var since = parts.Length > 0 && long.TryParse(parts[0], out var ticks)
            ? new DateTime(Math.Max(DateTime.MinValue.Ticks, Math.Min(ticks, DateTime.UtcNow.Ticks)), DateTimeKind.Utc)
            : DateTime.MinValue;
        var music = parts.Length > 1 && long.TryParse(parts[1], out var m) ? Math.Max(0, m) : 0;
        var list = parts.Length > 2 && long.TryParse(parts[2], out var p) ? Math.Max(0, p) : 0;
        return (since, music, list);
    }

    private static string BuildCursor(DateTime since, long musicId, long playlistId) =>
        $"{since.Ticks}:{musicId}:{playlistId}";

    public async Task<SyncDelta> GetDeltaAsync(Guid userId, string cursor, CancellationToken ct = default)
    {
        var (since, musicId, playlistId) = ParseCursor(cursor);
        var watermark = since;

        List<SongRowDto>? songs = null;
        var songRows = await db.Songs.Where(s => s.UserId == userId && s.UpdatedAtUtc >= since)
            .Select(s => new { s.UpdatedAtUtc, Row = new SongRowDto(s.File, s.DownloadedLink, s.Title, s.DateDownloadUtc, s.Artist, s.LoudnessDb) })
            .ToListAsync(ct);
        if (songRows.Count > 0)
        {
            songs = songRows.Select(x => x.Row).ToList();
            watermark = songRows.Max(x => x.UpdatedAtUtc) > watermark ? songRows.Max(x => x.UpdatedAtUtc) : watermark;
        }

        List<PodcastRowDto>? podcasts = null;
        var podcastRows = await db.Podcasts.Where(p => p.UserId == userId && p.UpdatedAtUtc >= since)
            .Select(p => new { p.UpdatedAtUtc, Row = new PodcastRowDto(p.File, p.DownloadedLink, p.Title, p.DateDownloadUtc, p.Artist) })
            .ToListAsync(ct);
        if (podcastRows.Count > 0)
        {
            podcasts = podcastRows.Select(x => x.Row).ToList();
            if (podcastRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = podcastRows.Max(x => x.UpdatedAtUtc);
        }

        List<PlaylistRowDto>? playlists = null;
        var playlistRows = await db.Playlists.Where(p => p.UserId == userId && p.UpdatedAtUtc >= since)
            .Select(p => new { p.UpdatedAtUtc, Row = new PlaylistRowDto(p.Id, p.Title, p.Description, p.Thumbnail) })
            .ToListAsync(ct);
        if (playlistRows.Count > 0)
        {
            playlists = playlistRows.Select(x => x.Row).ToList();
            if (playlistRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = playlistRows.Max(x => x.UpdatedAtUtc);
        }

        List<SongLinkRowDto>? songLinks = null;
        var songLinkRows = await db.SongPlaylists.Where(l => l.UserId == userId && l.UpdatedAtUtc >= since)
            .Select(l => new { l.UpdatedAtUtc, Row = new SongLinkRowDto(l.Id, l.SongFile, l.PlaylistId, l.DateAddedUtc) })
            .ToListAsync(ct);
        if (songLinkRows.Count > 0)
        {
            songLinks = songLinkRows.Select(x => x.Row).ToList();
            if (songLinkRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = songLinkRows.Max(x => x.UpdatedAtUtc);
        }

        List<TagRowDto>? tags = null;
        var tagRows = await db.PodcastTags.Where(t => t.UserId == userId && t.UpdatedAtUtc >= since)
            .Select(t => new { t.UpdatedAtUtc, Row = new TagRowDto(t.Id, t.Name) })
            .ToListAsync(ct);
        if (tagRows.Count > 0)
        {
            tags = tagRows.Select(x => x.Row).ToList();
            if (tagRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = tagRows.Max(x => x.UpdatedAtUtc);
        }

        List<TagLinkRowDto>? tagLinks = null;
        var tagLinkRows = await db.PodcastTagLinks.Where(l => l.UserId == userId && l.UpdatedAtUtc >= since)
            .Select(l => new { l.UpdatedAtUtc, Row = new TagLinkRowDto(l.Id, l.PodcastFile, l.TagId) })
            .ToListAsync(ct);
        if (tagLinkRows.Count > 0)
        {
            tagLinks = tagLinkRows.Select(x => x.Row).ToList();
            if (tagLinkRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = tagLinkRows.Max(x => x.UpdatedAtUtc);
        }

        List<LyricRowDto>? lyrics = null;
        var lyricRows = await db.Lyrics.Where(l => l.UserId == userId && l.UpdatedAtUtc >= since)
            .Select(l => new { l.UpdatedAtUtc, Row = new LyricRowDto(l.Id, l.SongFile, l.LyricsJson, l.OffsetMs) })
            .ToListAsync(ct);
        if (lyricRows.Count > 0)
        {
            lyrics = lyricRows.Select(x => x.Row).ToList();
            if (lyricRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = lyricRows.Max(x => x.UpdatedAtUtc);
        }

        List<MixRowDto>? mixes = null;
        var mixRows = await db.DailyMixes.Where(m => m.UserId == userId && m.UpdatedAtUtc >= since)
            .Select(m => new { m.UpdatedAtUtc, Row = new MixRowDto(m.MixDate, m.SongFilesJson) })
            .ToListAsync(ct);
        if (mixRows.Count > 0)
        {
            mixes = mixRows.Select(x => x.Row).ToList();
            if (mixRows.Max(x => x.UpdatedAtUtc) > watermark) watermark = mixRows.Max(x => x.UpdatedAtUtc);
        }

        var musicHistory = await db.MusicHistory.Where(h => h.UserId == userId && h.Id > musicId)
            .Select(h => new MusicHistoryRowDto(h.Id, h.SongFile, h.DatePlayedUtc))
            .ToListAsync(ct);
        var playlistHistory = await db.PlaylistHistory.Where(h => h.UserId == userId && h.Id > playlistId)
            .Select(h => new PlaylistHistoryRowDto(h.Id, h.PlaylistId, h.DatePlayedUtc))
            .ToListAsync(ct);

        List<DeletionRowDto>? deletions = null;
        var deletionRows = await db.SyncDeletions.Where(d => d.UserId == userId && d.DeletedAtUtc >= since)
            .Select(d => new { d.DeletedAtUtc, d.Id, Row = new DeletionRowDto(d.TableName, d.RowKey) })
            .ToListAsync(ct);
        if (deletionRows.Count > 0)
        {
            deletions = deletionRows.Select(x => x.Row).ToList();
            if (deletionRows.Max(x => x.DeletedAtUtc) > watermark) watermark = deletionRows.Max(x => x.DeletedAtUtc);
        }

        if (musicHistory.Count > 0) musicId = Math.Max(musicId, musicHistory.Max(h => h.Id));
        if (playlistHistory.Count > 0) playlistId = Math.Max(playlistId, playlistHistory.Max(h => h.Id));

        return new SyncDelta(BuildCursor(watermark, musicId, playlistId), new SyncSnapshot(
            songs, podcasts, playlists, songLinks, tags, tagLinks, lyrics,
            musicHistory.Count > 0 ? musicHistory : null,
            playlistHistory.Count > 0 ? playlistHistory : null,
            mixes, deletions));
    }

    public async Task SaveFileAsync(Guid userId, string file, bool isPodcast, byte[] bytes,
        CancellationToken ct = default)
    {
        if (storage is null) throw new InvalidOperationException("Library storage is not configured.");
        var path = isPodcast ? storage.PodcastPath(userId, file) : storage.SongPath(userId, file);
        await File.WriteAllBytesAsync(path, bytes, ct);
        var title = Path.GetFileNameWithoutExtension(file);
        if (isPodcast)
        {
            var existing = await db.Podcasts.FindAsync([userId, file], ct);
            if (existing is null)
                await db.Podcasts.AddAsync(new Podcast
                {
                    UserId = userId, File = file, Title = title,
                    DateDownloadUtc = DateTime.UtcNow,
                }, ct);
            else
                existing.DateDownloadUtc = DateTime.UtcNow;
        }
        else
        {
            var existing = await db.Songs.FindAsync([userId, file], ct);
            if (existing is null)
                await db.Songs.AddAsync(new Song
                {
                    UserId = userId, File = file, Title = title,
                    DateDownloadUtc = DateTime.UtcNow,
                }, ct);
            else
                existing.DateDownloadUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<ImportResult> ImportAsync(Guid userId, SyncSnapshot s, CancellationToken ct = default,
        bool reconcileLinks = false)
    {
        var counts = new int[11];
        // Explicit snapshot ids come from per-device sequences and collide
        // across users (single-column PKs are one global namespace, like the
        // desktop remote DB). Colliding rows are re-keyed; FKs are remapped.
        var playlistRemap = new Dictionary<long, long>();
        var tagRemap = new Dictionary<long, long>();
        // Link reconciliation (pull path only): links are compared by natural
        // key (parent id + file) post-remap, never by colliding numeric ids.
        HashSet<(long Parent, string File)>? keepSongLinks = reconcileLinks ? [] : null;
        HashSet<(long Parent, string File)>? keepTagLinks = reconcileLinks ? [] : null;

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
                keepSongLinks?.Add((pid, r.SongFile));
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
                keepTagLinks?.Add((tid, r.PodcastFile));
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

        // Pull reconciliation: drop local link rows absent from a full remote
        // snapshot (unlinks + parent-deletes with cascaded remote links).
        // Natural keys post-remap — numeric ids collide across devices.
        // Skipped for partial snapshots (null section), for empty remote
        // sets (a fresh/failed remote must never wipe links), and for
        // /sync/import (reconcileLinks false): partial payloads stay safe.
        if (reconcileLinks && s.SongLinks is { Count: > 0 } && keepSongLinks is not null)
        {
            var local = await db.SongPlaylists.Where(l => l.UserId == userId).ToListAsync(ct);
            foreach (var l in local)
                if (!keepSongLinks.Contains((l.PlaylistId, l.SongFile)))
                {
                    db.SongPlaylists.Remove(l);
                    counts[10]++;
                }
        }
        if (reconcileLinks && s.PodcastTagLinks is { Count: > 0 } && keepTagLinks is not null)
        {
            var local = await db.PodcastTagLinks.Where(l => l.UserId == userId).ToListAsync(ct);
            foreach (var l in local)
                if (!keepTagLinks.Contains((l.TagId, l.PodcastFile)))
                {
                    db.PodcastTagLinks.Remove(l);
                    counts[10]++;
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

        var summary = new SyncSummary(counts[0], counts[1], counts[2], counts[3], counts[4],
            counts[5], counts[6], counts[7], counts[8], counts[9], counts[10]);

        // Files the catalog references but the disk lacks: the pushing
        // device uploads these via PUT /sync/files/{file} to complete the
        // push (Exists guards traversal — bad names report missing and the
        // PUT path rejects them).
        var missing = new List<string>();
        if (storage is not null)
        {
            if (s.Songs is not null)
                foreach (var r in s.Songs)
                    if (!string.IsNullOrWhiteSpace(r.File) && !storage.SongExists(userId, r.File))
                        missing.Add(r.File);
            if (s.Podcasts is not null)
                foreach (var r in s.Podcasts)
                    if (!string.IsNullOrWhiteSpace(r.File) && !storage.PodcastExists(userId, r.File))
                        missing.Add(r.File);
        }
        return new ImportResult(summary, missing);
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
