using Hathor.Application.Ingest;
using Hathor.Application.Ports;
using Hathor.Application.Sync;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Hathor.Infrastructure.Sync;

// Per-library remote pull (desktop DatabaseManager.
// sync_remote_to_local_and_download, split in two): read rows from the
// desktop TiDB remote (same DB_HOST/DB_PORT/DB_USER/DB_PASSWORD/DB_NAME env
// the desktop app syncs to), merge them through the snapshot importer
// (cross-user id remap included), then queue downloads for files missing
// on disk. Failures come back as messages (desktop behavior), not throws.
public sealed class RemotePullService(
    ISyncService sync,
    IDownloadQueue queue,
    ILibraryStorage storage,
    RemoteDbOptions options,
    ILogger<RemotePullService> log) : IRemotePullService
{
    public async Task<RemotePullResult> PullSongsAsync(Guid userId, CancellationToken ct = default)
    {
        var connStr = options.ConnectionString();
        if (connStr is null)
            return new RemotePullResult(0, 0, "No remote DB configured. Remote sync unavailable.");

        List<RemoteSong> songs;
        List<PlaylistRowDto> playlists;
        List<SongLinkRowDto> links;
        List<LyricRowDto> lyrics;
        List<MusicHistoryRowDto> musicHistory;
        List<PlaylistHistoryRowDto> playlistHistory;
        List<MixRowDto> mixes;
        try
        {
            await using var conn = await RemoteMySql.OpenAsync(connStr, log, "pull-songs", ct);
            if (!await TableExistsAsync(conn, "songs", ct))
                return new RemotePullResult(0, 0, "Remote database not initialized yet.");
            songs = await QueryAsync(conn,
                "SELECT file, downloaded_link, title, date_download, artist FROM songs",
                r => new RemoteSong(
                    r.GetString(0), NullableText(r, 1), r.GetString(2),
                    UtcDate(r, 3), NullableText(r, 4)), ct);
            playlists = await QueryAsync(conn,
                "SELECT id, title, description, thumbnail FROM playlists",
                r => new PlaylistRowDto(r.GetInt64(0), r.GetString(1), NullableText(r, 2), NullableText(r, 3)), ct);
            links = await QueryAsync(conn,
                "SELECT id, song_file, playlist_id, date_added FROM song_playlist",
                r => new SongLinkRowDto(r.GetInt64(0), r.GetString(1), r.GetInt64(2), UtcDate(r, 3)), ct);
            // offset_ms postdates desktop remotes: read it when present.
            var lyricsSql = await ColumnExistsAsync(conn, "lyrics", "offset_ms", ct)
                ? "SELECT id, song_file, lyrics, offset_ms FROM lyrics"
                : "SELECT id, song_file, lyrics FROM lyrics";
            lyrics = await QueryAsync(conn, lyricsSql,
                r => new LyricRowDto(r.GetInt64(0), r.GetString(1), NullableText(r, 2),
                    r.FieldCount > 3 && !r.IsDBNull(3) ? r.GetInt32(3) : 0), ct);
            musicHistory = await QueryAsync(conn,
                "SELECT id, song_file, date_played FROM music_history",
                r => new MusicHistoryRowDto(r.GetInt64(0), r.GetString(1), UtcDate(r, 2)), ct);
            playlistHistory = await QueryAsync(conn,
                "SELECT id, playlist_id, date_played FROM playlist_history",
                r => new PlaylistHistoryRowDto(r.GetInt64(0), r.GetInt64(1), UtcDate(r, 2)), ct);
            // daily_mix postdates some remote DBs (desktop guarded-table behavior).
            mixes = await TryQueryAsync(conn,
                "SELECT mix_date, song_files FROM daily_mix",
                r => new MixRowDto(r.GetString(0), r.GetString(1)), ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remote song pull failed: could not read remote DB");
            return new RemotePullResult(0, 0, RemoteMySql.Describe("Error connecting to remote DB", ex));
        }

        SyncSummary summary;
        try
        {
            summary = await sync.ImportAsync(userId, new SyncSnapshot(
                Songs: songs.Select(s => new SongRowDto(
                    s.File, s.DownloadedLink, s.Title, s.DateDownload, s.Artist)).ToList(),
                Podcasts: null,
                Playlists: playlists,
                SongLinks: links,
                PodcastTags: null,
                PodcastTagLinks: null,
                Lyrics: lyrics,
                MusicHistory: musicHistory,
                PlaylistHistory: playlistHistory,
                DailyMix: mixes,
                Deletions: null), ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remote song pull failed: could not merge into local DB");
            return new RemotePullResult(0, 0, $"Error syncing to local DB: {ex.Message}");
        }

        var downloads = 0;
        foreach (var s in songs)
        {
            if (storage.SongExists(userId, s.File)) continue;
            try
            {
                // TargetFile pins the exact remote filename on disk so the
                // merged row resolves (remote TiDB → database AND disk).
                await queue.SubmitAsync(userId, DownloadUrl(s.DownloadedLink, s.Title, s.Artist),
                    s.Title, string.IsNullOrWhiteSpace(s.Artist) ? null : s.Artist, false, ct,
                    targetFile: s.File);
                downloads++;
            }
            catch (Exception ex)
            {
                // One bad row must not abort the pull (desktop skips the same way).
                log.LogWarning(ex, "Remote song pull: skipping failed download for {File}", s.File);
            }
        }

        return new RemotePullResult(summary.Songs, downloads,
            $"Synced {summary.Songs} new entries from remote DB. Started downloading {downloads} songs.");
    }

    public async Task<RemotePullResult> PullPodcastsAsync(Guid userId, CancellationToken ct = default)
    {
        var connStr = options.ConnectionString();
        if (connStr is null)
            return new RemotePullResult(0, 0, "No remote DB configured. Remote sync unavailable.");

        List<RemoteSong> episodes;
        List<TagRowDto> tags;
        List<TagLinkRowDto> tagLinks;
        try
        {
            await using var conn = await RemoteMySql.OpenAsync(connStr, log, "pull-podcasts", ct);
            if (!await TableExistsAsync(conn, "podcasts", ct))
                return new RemotePullResult(0, 0, "Remote database not initialized yet.");
            episodes = await QueryAsync(conn,
                "SELECT file, downloaded_link, title, date_download, artist FROM podcasts",
                r => new RemoteSong(
                    r.GetString(0), NullableText(r, 1), r.GetString(2),
                    UtcDate(r, 3), NullableText(r, 4)), ct);
            // podcast_tags / podcast_tag_links postdate some remote DBs.
            tags = await TryQueryAsync(conn,
                "SELECT id, name FROM podcast_tags",
                r => new TagRowDto(r.GetInt64(0), r.GetString(1)), ct);
            tagLinks = await TryQueryAsync(conn,
                "SELECT id, podcast_file, tag_id FROM podcast_tag_links",
                r => new TagLinkRowDto(r.GetInt64(0), r.GetString(1), r.GetInt64(2)), ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remote podcast pull failed: could not read remote DB");
            return new RemotePullResult(0, 0, RemoteMySql.Describe("Error connecting to remote DB", ex));
        }

        SyncSummary summary;
        try
        {
            summary = await sync.ImportAsync(userId, new SyncSnapshot(
                Songs: null,
                Podcasts: episodes.Select(e => new PodcastRowDto(
                    e.File, e.DownloadedLink, e.Title, e.DateDownload, e.Artist)).ToList(),
                Playlists: null,
                SongLinks: null,
                PodcastTags: tags,
                PodcastTagLinks: tagLinks,
                Lyrics: null,
                MusicHistory: null,
                PlaylistHistory: null,
                DailyMix: null,
                Deletions: null), ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remote podcast pull failed: could not merge into local DB");
            return new RemotePullResult(0, 0, $"Error syncing to local DB: {ex.Message}");
        }

        var downloads = 0;
        foreach (var e in episodes)
        {
            if (storage.PodcastExists(userId, e.File)) continue;
            try
            {
                await queue.SubmitAsync(userId, DownloadUrl(e.DownloadedLink, e.Title, e.Artist),
                    e.Title, string.IsNullOrWhiteSpace(e.Artist) ? null : e.Artist, true, ct,
                    targetFile: e.File);
                downloads++;
            }
            catch (Exception ex)
            {
                // One bad row must not abort the pull.
                log.LogWarning(ex, "Remote podcast pull: skipping failed download for {File}", e.File);
            }
        }

        return new RemotePullResult(summary.Podcasts, downloads,
            $"Synced {summary.Podcasts} new entries from remote DB. Started downloading {downloads} podcasts.");
    }

    // Desktop download-target rule: reuse the stored link, else fall back to
    // a "<title> <artist> audio" search (the queue resolves both).
    private static string DownloadUrl(string? downloadedLink, string title, string? artist) =>
        string.IsNullOrWhiteSpace(downloadedLink)
            ? $"{title} {artist ?? ""} audio".Trim()
            : downloadedLink;

    private static async Task<bool> TableExistsAsync(MySqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.tables " +
            "WHERE table_schema = DATABASE() AND table_name = @table", conn);
        cmd.Parameters.AddWithValue("@table", table);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    private static async Task<List<T>> QueryAsync<T>(
        MySqlConnection conn, string sql, Func<MySqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }

    private static async Task<List<T>> TryQueryAsync<T>(
        MySqlConnection conn, string sql, Func<MySqlDataReader, T> map, CancellationToken ct)
    {
        try
        {
            return await QueryAsync(conn, sql, map, ct);
        }
        catch
        {
            return new List<T>();
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        MySqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns " +
            "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column", conn);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static string? NullableText(MySqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static DateTime UtcDate(MySqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal)
            ? DateTime.UtcNow
            : DateTime.SpecifyKind(r.GetDateTime(ordinal), DateTimeKind.Utc);

    private sealed record RemoteSong(
        string File, string? DownloadedLink, string Title, DateTime DateDownload, string? Artist);
}

// Connection settings for the desktop TiDB remote. appsettings
// "RemoteDb" section wins; bare DB_* env names (desktop sync.py parity)
// fill whatever is missing so existing compose files keep working.
public sealed class RemoteDbOptions
{
    public string? Host { get; init; }
    public int Port { get; init; } = 4000;
    public string? User { get; init; }
    public string? Password { get; init; }
    public string? Database { get; init; }
    public string? SslCa { get; init; }

    public string? ConnectionString()
    {
        var host = Host ?? Environment.GetEnvironmentVariable("DB_HOST");
        if (string.IsNullOrWhiteSpace(host)) return null;
        var port = Port != 4000 ? Port : int.TryParse(
            Environment.GetEnvironmentVariable("DB_PORT"), out var p) ? p : 4000;
        var user = User ?? Environment.GetEnvironmentVariable("DB_USER") ?? "";
        var password = Password ?? Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";
        var database = Database ?? Environment.GetEnvironmentVariable("DB_NAME") ?? "";
        var sslCa = SslCa ?? Environment.GetEnvironmentVariable("DB_SSL_CA");
        var ssl = !string.IsNullOrWhiteSpace(sslCa) && File.Exists(sslCa)
            ? $"SslCa={sslCa};SslMode=VerifyCA"
            : "SslMode=Preferred";
        // Generous connect timeout: serverless remotes (TiDB Cloud free
        // tier) pause when idle and can need ~a minute to resume.
        return $"Server={host};Port={port};User ID={user};Password={password};" +
            $"Database={database};Connection Timeout=60;{ssl}";
    }
}

// Shared remote-MySQL open + error shaping (TiDB Cloud serverless pauses
// when idle: the first open wakes it and can time out, so retry once).
internal static class RemoteMySql
{
    public static async Task<MySqlConnection> OpenAsync(
        string connectionString, ILogger log, string operation, CancellationToken ct)
    {
        var conn = new MySqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (MySqlException ex)
        {
            // Transient transport/DNS/redirect blips (and waking
            // serverless clusters): one retry before giving up.
            log.LogWarning(ex,
                "Remote DB connect failed ({Operation} {Endpoint}); retrying once.",
                operation, EndpointOf(connectionString));
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            await conn.OpenAsync(ct);
            return conn;
        }
    }

    public static string Describe(string prefix, Exception ex) =>
        ex is MySqlException mex && IsConnectTimeout(mex)
            ? $"{prefix}: {ex.Message} (the remote cluster may be waking from sleep — try again in a minute)."
            : $"{prefix}: {ex.Message}";

    internal static bool IsConnectTimeout(MySqlException ex) =>
        ex.Message.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase);

    // Host:port only — never credentials — for logs and UI messages.
    internal static string EndpointOf(string connectionString)
    {
        try
        {
            var b = new MySqlConnectionStringBuilder(connectionString);
            return $"{b.Server}:{b.Port}";
        }
        catch
        {
            return "unknown host";
        }
    }
}
