using Hathor.Application.Ports;
using Hathor.Application.Sync;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Hathor.Infrastructure.Sync;

// Remote push (desktop DatabaseSync.sync_once/_run_sync, split per
// library): write this user's rows into the desktop TiDB remote. The remote
// is one global namespace (no UserId), so multi-user collisions resolve
// last-writer-wins exactly like desktop multi-device sync. Link tables are
// replaced scoped to this user's playlists/tags (desktop wipes the whole
// table — unsafe with several users sharing one remote).
public sealed class RemotePushService(
    HathorDbContext db,
    RemoteDbOptions options,
    ILogger<RemotePushService> log) : IRemotePushService
{
    // Desktop REMOTE_SCHEMA (sync.py) + daily_mix (pushed but never created
    // there — a fresh remote would abort the whole push on that table).
    private static readonly string[] RemoteSchema =
    [
        """
        CREATE TABLE IF NOT EXISTS songs (
            file VARCHAR(255) PRIMARY KEY,
            downloaded_link VARCHAR(255),
            title VARCHAR(255) NOT NULL,
            date_download TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            artist VARCHAR(255),
            loudness_db DOUBLE NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS playlists (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            title VARCHAR(255) NOT NULL,
            description TEXT,
            thumbnail TEXT
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS song_playlist (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            song_file VARCHAR(255) NOT NULL,
            playlist_id BIGINT NOT NULL,
            date_added TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS lyrics (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            song_file VARCHAR(255) NOT NULL,
            lyrics TEXT,
            offset_ms INT NOT NULL DEFAULT 0
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS music_history (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            song_file VARCHAR(255) NOT NULL,
            date_played TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS playlist_history (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            playlist_id BIGINT NOT NULL,
            date_played TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS daily_mix (
            mix_date VARCHAR(10) PRIMARY KEY,
            song_files TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS podcasts (
            file VARCHAR(255) PRIMARY KEY,
            downloaded_link VARCHAR(255),
            title VARCHAR(255) NOT NULL,
            date_download TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            artist VARCHAR(255)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS podcast_tags (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            name VARCHAR(255) NOT NULL UNIQUE
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS podcast_tag_links (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            podcast_file VARCHAR(255) NOT NULL,
            tag_id BIGINT NOT NULL
        )
        """,
    ];

    // Desktop REMOTE_DELETE_COLUMNS (tombstone table → remote key column).
    private static readonly Dictionary<string, string> DeleteColumns = new()
    {
        ["songs"] = "file",
        ["podcasts"] = "file",
        ["playlists"] = "id",
        ["lyrics"] = "song_file",
        ["music_history"] = "song_file",
        ["playlist_history"] = "playlist_id",
        ["podcast_tags"] = "id",
    };

    private static readonly string[] SongTables =
        ["songs", "playlists", "lyrics", "music_history", "playlist_history"];
    private static readonly string[] PodcastTables = ["podcasts", "podcast_tags"];

    public async Task<RemotePushResult> PushSongsAsync(Guid userId, CancellationToken ct = default)
    {
        var connStr = options.ConnectionString();
        if (connStr is null)
            return new RemotePushResult(0, "No remote DB configured. Remote sync unavailable.");

        var songs = await db.Songs.Where(s => s.UserId == userId)
            .Select(s => new object?[] { s.File, s.DownloadedLink, s.Title, s.DateDownloadUtc, s.Artist, s.LoudnessDb })
            .ToListAsync(ct);
        var playlists = await db.Playlists.Where(p => p.UserId == userId)
            .Select(p => new object?[] { p.Id, p.Title, p.Description, p.Thumbnail })
            .ToListAsync(ct);
        var links = await db.SongPlaylists.Where(l => l.UserId == userId)
            .Select(l => new object?[] { l.Id, l.SongFile, l.PlaylistId, l.DateAddedUtc })
            .ToListAsync(ct);
        var lyrics = await db.Lyrics.Where(l => l.UserId == userId
                // Chapter cache rows ("{file}::chapter:{id}") are web-side only:
                // the desktop never reads them, so don't plant phantoms.
                && !l.SongFile.Contains(Application.Lyrics.SyncedLyricsGuards.ChapterKeyMarker))
            .Select(l => new object?[] { l.Id, l.SongFile, l.LyricsJson, l.OffsetMs })
            .ToListAsync(ct);
        var mixes = await db.DailyMixes.Where(m => m.UserId == userId)
            .Select(m => new object?[] { m.MixDate, m.SongFilesJson })
            .ToListAsync(ct);
        var playlistIds = await db.Playlists.Where(p => p.UserId == userId)
            .Select(p => p.Id).ToListAsync(ct);
        var tombstones = await db.SyncDeletions
            .Where(d => d.UserId == userId && SongTables.Contains(d.TableName))
            .Select(d => new { d.TableName, d.RowKey }).ToListAsync(ct);

        try
        {
            await using var conn = await RemoteMySql.OpenAsync(connStr, log, "push-songs", ct);
            await InitSchemaAsync(conn, ct);
            var rows = 0;
            rows += await ApplyDeletionsAsync(conn, tombstones
                .Select(t => (t.TableName, t.RowKey)).ToList(), ct);
            rows += await UpsertAsync(conn, "songs",
                ["file", "downloaded_link", "title", "date_download", "artist", "loudness_db"],
                "downloaded_link = VALUES(downloaded_link), title = VALUES(title), " +
                "date_download = VALUES(date_download), artist = VALUES(artist), " +
                // Adopt measurements, never wipe a remote one with a local null.
                "loudness_db = COALESCE(VALUES(loudness_db), loudness_db)",
                songs, ct);
            rows += await UpsertAsync(conn, "playlists",
                ["id", "title", "description", "thumbnail"],
                "title = VALUES(title), description = VALUES(description), thumbnail = VALUES(thumbnail)",
                playlists, ct);
            await AlignAutoIncrementAsync(conn, "playlists", ct);
            rows += await ReplaceLinksAsync(conn, "song_playlist",
                ["id", "song_file", "playlist_id", "date_added"], "playlist_id", playlistIds, links, ct);
            rows += await UpsertAsync(conn, "lyrics",
                ["id", "song_file", "lyrics", "offset_ms"],
                "song_file = VALUES(song_file), lyrics = VALUES(lyrics), offset_ms = VALUES(offset_ms)",
                lyrics, ct);
            await AlignAutoIncrementAsync(conn, "lyrics", ct);
            rows += await PushDailyMixAsync(conn, mixes, ct);
            rows += await PushHistoryAsync(conn, "music_history",
                ["id", "song_file", "date_played"],
                await db.MusicHistory.Where(h => h.UserId == userId)
                    .OrderBy(h => h.Id)
                    .Select(h => new object?[] { h.Id, h.SongFile, h.DatePlayedUtc })
                    .ToListAsync(ct), ct);
            rows += await PushHistoryAsync(conn, "playlist_history",
                ["id", "playlist_id", "date_played"],
                await db.PlaylistHistory.Where(h => h.UserId == userId)
                    .OrderBy(h => h.Id)
                    .Select(h => new object?[] { h.Id, h.PlaylistId, h.DatePlayedUtc })
                    .ToListAsync(ct), ct);
            await ClearTombstonesAsync(userId, SongTables, ct);
            return new RemotePushResult(rows, $"Pushed {rows} rows to remote DB.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remote song push failed");
            return new RemotePushResult(0,
                RemoteMySql.Describe($"Error pushing to remote DB [{RemoteMySql.EndpointOf(connStr)}]", ex));
        }
    }

    public async Task<RemotePushResult> PushPodcastsAsync(Guid userId, CancellationToken ct = default)
    {
        var connStr = options.ConnectionString();
        if (connStr is null)
            return new RemotePushResult(0, "No remote DB configured. Remote sync unavailable.");

        var episodes = await db.Podcasts.Where(p => p.UserId == userId)
            .Select(p => new object?[] { p.File, p.DownloadedLink, p.Title, p.DateDownloadUtc, p.Artist })
            .ToListAsync(ct);
        var tags = await db.PodcastTags.Where(t => t.UserId == userId)
            .Select(t => new object?[] { t.Id, t.Name })
            .ToListAsync(ct);
        var tagLinks = await db.PodcastTagLinks.Where(l => l.UserId == userId)
            .Select(l => new object?[] { l.Id, l.PodcastFile, l.TagId })
            .ToListAsync(ct);
        var tagIds = tags.Select(t => (long)t[0]!).ToList();
        var tombstones = await db.SyncDeletions
            .Where(d => d.UserId == userId && PodcastTables.Contains(d.TableName))
            .Select(d => new { d.TableName, d.RowKey }).ToListAsync(ct);

        try
        {
            await using var conn = await RemoteMySql.OpenAsync(connStr, log, "push-podcasts", ct);
            await InitSchemaAsync(conn, ct);
            var rows = 0;
            rows += await ApplyDeletionsAsync(conn, tombstones
                .Select(t => (t.TableName, t.RowKey)).ToList(), ct);
            rows += await UpsertAsync(conn, "podcasts",
                ["file", "downloaded_link", "title", "date_download", "artist"],
                "downloaded_link = VALUES(downloaded_link), title = VALUES(title), " +
                "date_download = VALUES(date_download), artist = VALUES(artist)",
                episodes, ct);
            rows += await UpsertAsync(conn, "podcast_tags",
                ["id", "name"],
                "name = VALUES(name)",
                tags, ct);
            await AlignAutoIncrementAsync(conn, "podcast_tags", ct);
            rows += await ReplaceLinksAsync(conn, "podcast_tag_links",
                ["id", "podcast_file", "tag_id"], "tag_id", tagIds, tagLinks, ct);
            await ClearTombstonesAsync(userId, PodcastTables, ct);
            return new RemotePushResult(rows, $"Pushed {rows} rows to remote DB.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remote podcast push failed");
            return new RemotePushResult(0,
                RemoteMySql.Describe($"Error pushing to remote DB [{RemoteMySql.EndpointOf(connStr)}]", ex));
        }
    }

    private static async Task InitSchemaAsync(MySqlConnection conn, CancellationToken ct)
    {
        foreach (var ddl in RemoteSchema)
        {
            await using var cmd = new MySqlCommand(ddl, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Migrate remotes created before the thumbnail / offset / loudness columns existed.
        try
        {
            await using var alter = new MySqlCommand(
                "ALTER TABLE playlists ADD COLUMN thumbnail TEXT", conn);
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Column already there — desktop ignores this the same way.
        }
        try
        {
            await using var alter = new MySqlCommand(
                "ALTER TABLE lyrics ADD COLUMN offset_ms INT NOT NULL DEFAULT 0", conn);
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Column already there.
        }
        try
        {
            await using var alter = new MySqlCommand(
                "ALTER TABLE songs ADD COLUMN loudness_db DOUBLE NULL", conn);
            await alter.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Column already there.
        }
    }

    private static async Task<int> ApplyDeletionsAsync(
        MySqlConnection conn, List<(string Table, string Key)> tombstones, CancellationToken ct)
    {
        var applied = 0;
        foreach (var (table, key) in tombstones)
        {
            if (!DeleteColumns.TryGetValue(table, out var col)) continue;
            await using var cmd = new MySqlCommand(
                $"DELETE FROM `{table}` WHERE `{col}` = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            applied += await cmd.ExecuteNonQueryAsync(ct);
            // Link rows have no tombstones of their own: cascade the parent
            // delete so orphans don't linger (pull reconciliation removes
            // local copies of links missing remotely).
            foreach (var (linkTable, linkCol) in LinkCascadeColumns(table))
            {
                await using var cascade = new MySqlCommand(
                    $"DELETE FROM `{linkTable}` WHERE `{linkCol}` = @key", conn);
                cascade.Parameters.AddWithValue("@key", key);
                applied += await cascade.ExecuteNonQueryAsync(ct);
            }
        }
        return applied;
    }

    // Parent tombstone table → (link table, link column). Link rows carry no
    // tombstones of their own; cascade by the pushed key itself (rows this
    // device created) — never by remote id, which is a per-device sequence.
    private static IEnumerable<(string Table, string Column)> LinkCascadeColumns(string table) =>
        table switch
        {
            "songs" => [("song_playlist", "song_file")],
            "podcasts" => [("podcast_tag_links", "podcast_file")],
            "playlists" => [("song_playlist", "playlist_id")],
            "podcast_tags" => [("podcast_tag_links", "tag_id")],
            _ => [],
        };

    // Chunked multi-row upsert (desktop executemany equivalent — one round
    // trip per chunk instead of per row).
    private static async Task<int> UpsertAsync(
        MySqlConnection conn, string table, string[] cols, string updateClause,
        List<object?[]> rows, CancellationToken ct, int chunk = 250)
    {
        var written = 0;
        foreach (var batch in rows.Chunk(chunk))
        {
            var values = new List<string>();
            await using var cmd = new MySqlCommand();
            cmd.Connection = conn;
            var i = 0;
            foreach (var row in batch)
            {
                var placeholders = new List<string>();
                foreach (var value in row)
                {
                    var name = $"@p{i++}";
                    placeholders.Add(name);
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                values.Add($"({string.Join(", ", placeholders)})");
            }
            cmd.CommandText =
                $"INSERT INTO `{table}` ({string.Join(", ", cols.Select(c => $"`{c}`"))}) " +
                $"VALUES {string.Join(", ", values)} ON DUPLICATE KEY UPDATE {updateClause}";
            written += await cmd.ExecuteNonQueryAsync(ct);
        }
        return written;
    }

    // Scoped link-table replace: delete only rows pointing at this user's
    // parents, then insert the current links (desktop deletes everything —
    // that would wipe other users' links off the shared remote).
    private static async Task<int> ReplaceLinksAsync(
        MySqlConnection conn, string table, string[] cols, string parentCol,
        List<long> parentIds, List<object?[]> links, CancellationToken ct)
    {
        if (parentIds.Count == 0) return 0;
        var written = 0;
        foreach (var batch in parentIds.Chunk(500))
        {
            await using var del = new MySqlCommand(
                $"DELETE FROM `{table}` WHERE `{parentCol}` IN ({string.Join(", ", batch)})", conn);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var batch in links.Chunk(250))
        {
            var values = new List<string>();
            await using var cmd = new MySqlCommand();
            cmd.Connection = conn;
            var i = 0;
            foreach (var row in batch)
            {
                var placeholders = new List<string>();
                foreach (var value in row)
                {
                    var name = $"@p{i++}";
                    placeholders.Add(name);
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                values.Add($"({string.Join(", ", placeholders)})");
            }
            cmd.CommandText =
                $"INSERT INTO `{table}` ({string.Join(", ", cols.Select(c => $"`{c}`"))}) " +
                $"VALUES {string.Join(", ", values)}";
            written += await cmd.ExecuteNonQueryAsync(ct);
        }
        await AlignAutoIncrementAsync(conn, table, ct);
        return written;
    }

    // Local mixes win per date; prune remote mixes older than our newest
    // (desktop rule — an outdated device can never delete a newer mix).
    private static async Task<int> PushDailyMixAsync(
        MySqlConnection conn, List<object?[]> mixes, CancellationToken ct)
    {
        if (mixes.Count == 0) return 0;
        var newest = mixes.Select(m => (string)m[0]!).Max();
        await using var prune = new MySqlCommand(
            "DELETE FROM daily_mix WHERE mix_date < @newest", conn);
        prune.Parameters.AddWithValue("@newest", newest);
        await prune.ExecuteNonQueryAsync(ct);
        return await UpsertAsync(conn, "daily_mix",
            ["mix_date", "song_files"], "song_files = VALUES(song_files)", mixes, ct);
    }

    // Append-only history: only rows past the remote MAX(id)
    // (desktop INSERT IGNORE semantics).
    private static async Task<int> PushHistoryAsync(
        MySqlConnection conn, string table, string[] cols,
        List<object?[]> rows, CancellationToken ct, int chunk = 500)
    {
        await using var maxCmd = new MySqlCommand(
            $"SELECT COALESCE(MAX(id), 0) FROM `{table}`", conn);
        var last = Convert.ToInt64(await maxCmd.ExecuteScalarAsync(ct));
        var fresh = rows.Where(r => Convert.ToInt64(r[0]) > last).ToList();
        var written = 0;
        foreach (var batch in fresh.Chunk(chunk))
        {
            var values = new List<string>();
            await using var cmd = new MySqlCommand();
            cmd.Connection = conn;
            var i = 0;
            foreach (var row in batch)
            {
                var placeholders = new List<string>();
                foreach (var value in row)
                {
                    var name = $"@p{i++}";
                    placeholders.Add(name);
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
                values.Add($"({string.Join(", ", placeholders)})");
            }
            cmd.CommandText =
                $"INSERT IGNORE INTO `{table}` ({string.Join(", ", cols.Select(c => $"`{c}`"))}) " +
                $"VALUES {string.Join(", ", values)}";
            written += await cmd.ExecuteNonQueryAsync(ct);
        }
        if (written > 0) await AlignAutoIncrementAsync(conn, table, ct);
        return written;
    }

    // Desktop _align_auto_increment after explicit-id inserts.
    private static async Task AlignAutoIncrementAsync(
        MySqlConnection conn, string table, CancellationToken ct)
    {
        await using var maxCmd = new MySqlCommand(
            $"SELECT COALESCE(MAX(id), 0) + 1 FROM `{table}`", conn);
        var next = Convert.ToInt64(await maxCmd.ExecuteScalarAsync(ct));
        if (next <= 1) return;
        await using var alter = new MySqlCommand(
            $"ALTER TABLE `{table}` AUTO_INCREMENT = {next}", conn);
        await alter.ExecuteNonQueryAsync(ct);
    }

    private async Task ClearTombstonesAsync(Guid userId, string[] tables, CancellationToken ct)
    {
        await db.SyncDeletions
            .Where(d => d.UserId == userId && tables.Contains(d.TableName))
            .ExecuteDeleteAsync(ct);
    }
}
