package com.musicplayer.android.data.remote

import android.util.Log
import com.musicplayer.android.data.db.AppDatabase
import java.sql.Connection
import java.sql.Timestamp

// Push-side writes to the shared remote (desktop sync.py DatabaseSync +
// web RemotePushService, 1:1). The remote is one global namespace
// (no user id): last-writer-wins, exactly like desktop multi-device sync.
// Link tables are replaced SCOPED to this phone's playlist/tag ids (a
// global wipe would delete other users' links off the shared remote).
// Tombstones propagate local deletes; history appends past remote MAX(id).
object RemoteWriter {
    private const val TAG = "RemoteWriter"

    // Desktop REMOTE_SCHEMA (sync.py) + daily_mix (pushed but never created
    // there — a fresh remote would abort the whole push on that table).
    private val remoteSchema = listOf(
        """CREATE TABLE IF NOT EXISTS songs (
            file VARCHAR(255) PRIMARY KEY,
            downloaded_link VARCHAR(255),
            title VARCHAR(255) NOT NULL,
            date_download TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            artist VARCHAR(255)
        )""",
        """CREATE TABLE IF NOT EXISTS playlists (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            title VARCHAR(255) NOT NULL,
            description TEXT,
            thumbnail TEXT
        )""",
        """CREATE TABLE IF NOT EXISTS song_playlist (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            song_file VARCHAR(255) NOT NULL,
            playlist_id BIGINT NOT NULL,
            date_added TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )""",
        """CREATE TABLE IF NOT EXISTS lyrics (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            song_file VARCHAR(255) NOT NULL,
            lyrics TEXT,
            offset_ms INT NOT NULL DEFAULT 0
        )""",
        """CREATE TABLE IF NOT EXISTS music_history (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            song_file VARCHAR(255) NOT NULL,
            date_played TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )""",
        """CREATE TABLE IF NOT EXISTS playlist_history (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            playlist_id BIGINT NOT NULL,
            date_played TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )""",
        """CREATE TABLE IF NOT EXISTS daily_mix (
            mix_date VARCHAR(10) PRIMARY KEY,
            song_files TEXT NOT NULL
        )""",
        """CREATE TABLE IF NOT EXISTS podcasts (
            file VARCHAR(255) PRIMARY KEY,
            downloaded_link VARCHAR(255),
            title VARCHAR(255) NOT NULL,
            date_download TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            artist VARCHAR(255)
        )""",
        """CREATE TABLE IF NOT EXISTS podcast_tags (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            name VARCHAR(255) NOT NULL UNIQUE
        )""",
        """CREATE TABLE IF NOT EXISTS podcast_tag_links (
            id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            podcast_file VARCHAR(255) NOT NULL,
            tag_id BIGINT NOT NULL
        )""",
    )

    // Desktop REMOTE_DELETE_COLUMNS (tombstone table -> remote key column).
    private val deleteColumns = mapOf(
        "songs" to "file",
        "podcasts" to "file",
        "playlists" to "id",
        "lyrics" to "song_file",
        "music_history" to "song_file",
        "playlist_history" to "playlist_id",
        "podcast_tags" to "id",
    )

    private val songTables = listOf(
        "songs", "playlists", "lyrics", "music_history", "playlist_history",
    )
    private val podcastTables = listOf("podcasts", "podcast_tags")

    data class PushReport(val rows: Int, val message: String)

    suspend fun pushSongs(db: AppDatabase): PushReport {
        val conn = RemoteDb.open()
        return try {
            initSchema(conn)
            var rows = 0
            rows += applyDeletions(
                conn,
                db.syncDeletionDao().forTables(songTables).map { it.tableName to it.rowKey },
            )
            rows += upsert(
                conn, "songs",
                listOf("file", "downloaded_link", "title", "date_download", "artist"),
                "downloaded_link = VALUES(downloaded_link), title = VALUES(title), " +
                    "date_download = VALUES(date_download), artist = VALUES(artist)",
                db.songDao().all().map {
                    listOf(it.file, it.downloadedLink, it.title, Timestamp(it.dateDownloadMillis), it.artist)
                },
            )
            val playlists = db.playlistDao().all()
            rows += upsert(
                conn, "playlists",
                listOf("id", "title", "description", "thumbnail"),
                "title = VALUES(title), description = VALUES(description), thumbnail = VALUES(thumbnail)",
                playlists.map { listOf(it.id, it.title, it.description, it.thumbnail) },
            )
            alignAutoIncrement(conn, "playlists")
            val playlistIds = playlists.map { it.id }
            rows += replaceLinks(
                conn, "song_playlist",
                listOf("id", "song_file", "playlist_id", "date_added"), "playlist_id", playlistIds,
                db.songPlaylistDao().all().map {
                    listOf(it.id, it.songFile, it.playlistId, Timestamp(it.dateAddedMillis))
                },
            )
            val lyrics = db.lyricDao().all()
            rows += upsert(
                conn, "lyrics",
                listOf("id", "song_file", "lyrics", "offset_ms"),
                "song_file = VALUES(song_file), lyrics = VALUES(lyrics), offset_ms = VALUES(offset_ms)",
                lyrics.map { listOf(it.id, it.songFile, it.lyrics, it.offsetMs) },
            )
            alignAutoIncrement(conn, "lyrics")
            rows += pushDailyMix(conn, db)
            rows += pushHistory(
                conn, "music_history",
                listOf("id", "song_file", "date_played"),
                db.musicHistoryDao().allOrdered().map {
                    listOf<Any?>(it.id, it.songFile, Timestamp(it.datePlayedMillis))
                },
            )
            rows += pushHistory(
                conn, "playlist_history",
                listOf("id", "playlist_id", "date_played"),
                db.playlistHistoryDao().allOrdered().map {
                    listOf<Any?>(it.id, it.playlistId, Timestamp(it.datePlayedMillis))
                },
            )
            db.syncDeletionDao().clearTables(songTables)
            PushReport(rows, "Pushed $rows rows to remote DB.")
        } finally {
            try { conn.close() } catch (_: Exception) { }
        }
    }

    suspend fun pushPodcasts(db: AppDatabase): PushReport {
        val conn = RemoteDb.open()
        return try {
            initSchema(conn)
            var rows = 0
            rows += applyDeletions(
                conn,
                db.syncDeletionDao().forTables(podcastTables).map { it.tableName to it.rowKey },
            )
            rows += upsert(
                conn, "podcasts",
                listOf("file", "downloaded_link", "title", "date_download", "artist"),
                "downloaded_link = VALUES(downloaded_link), title = VALUES(title), " +
                    "date_download = VALUES(date_download), artist = VALUES(artist)",
                db.podcastDao().all().map {
                    listOf(it.file, it.downloadedLink, it.title, Timestamp(it.dateDownloadMillis), it.artist)
                },
            )
            val tags = db.podcastTagDao().all()
            rows += upsert(
                conn, "podcast_tags",
                listOf("id", "name"), "name = VALUES(name)",
                tags.map { listOf(it.id, it.name) },
            )
            alignAutoIncrement(conn, "podcast_tags")
            rows += replaceLinks(
                conn, "podcast_tag_links",
                listOf("id", "podcast_file", "tag_id"), "tag_id", tags.map { it.id },
                db.podcastTagLinkDao().all().map { listOf(it.id, it.podcastFile, it.tagId) },
            )
            db.syncDeletionDao().clearTables(podcastTables)
            PushReport(rows, "Pushed $rows rows to remote DB.")
        } finally {
            try { conn.close() } catch (_: Exception) { }
        }
    }

    private fun initSchema(conn: Connection) {
        conn.createStatement().use { stmt ->
            remoteSchema.forEach(stmt::addBatch)
            stmt.executeBatch()
        }
        // Migrate remotes created before the thumbnail / offset columns existed.
        try {
            conn.createStatement().use {
                it.execute("ALTER TABLE playlists ADD COLUMN thumbnail TEXT")
            }
        } catch (_: Exception) {
            // Column already there — desktop ignores this the same way.
        }
        try {
            conn.createStatement().use {
                it.execute("ALTER TABLE lyrics ADD COLUMN offset_ms INT NOT NULL DEFAULT 0")
            }
        } catch (_: Exception) {
            // Column already there.
        }
    }

    private fun applyDeletions(
        conn: Connection, tombstones: List<Pair<String, String>>,
    ): Int {
        var applied = 0
        for ((table, key) in tombstones) {
            val col = deleteColumns[table] ?: continue
            conn.prepareStatement("DELETE FROM `$table` WHERE `$col` = ?").use { stmt ->
                stmt.setString(1, key)
                applied += stmt.executeUpdate()
            }
        }
        return applied
    }

    // Chunked multi-row upsert (desktop executemany equivalent — one round
    // trip per chunk instead of per row).
    private fun upsert(
        conn: Connection, table: String, cols: List<String>, updateClause: String,
        rows: List<List<Any?>>, chunk: Int = 250,
    ): Int {
        if (rows.isEmpty()) return 0
        var written = 0
        for (batch in rows.chunked(chunk)) {
            val single = "(${cols.joinToString(", ") { "?" }})"
            val sql = "INSERT INTO `$table` (${cols.joinToString(", ") { "`$it`" }}) " +
                "VALUES ${batch.joinToString(", ") { single }} " +
                "ON DUPLICATE KEY UPDATE $updateClause"
            conn.prepareStatement(sql).use { stmt ->
                var ix = 1
                for (row in batch) for (value in row) stmt.setObject(ix++, value)
                written += stmt.executeUpdate()
            }
        }
        return written
    }

    // Scoped link-table replace: delete only rows pointing at this phone's
    // parents, then insert the current links (desktop deletes everything —
    // that would wipe other users' links off the shared remote).
    private fun replaceLinks(
        conn: Connection, table: String, cols: List<String>, parentCol: String,
        parentIds: List<Long>, links: List<List<Any?>>,
    ): Int {
        if (parentIds.isEmpty()) return 0
        var written = 0
        for (batch in parentIds.chunked(500)) {
            conn.prepareStatement(
                "DELETE FROM `$table` WHERE `$parentCol` IN (${batch.joinToString(",")})",
            ).use { it.executeUpdate() }
        }
        for (batch in links.chunked(250)) {
            val single = "(${cols.joinToString(", ") { "?" }})"
            val sql = "INSERT INTO `$table` (${cols.joinToString(", ") { "`$it`" }}) " +
                "VALUES ${batch.joinToString(", ") { single }}"
            conn.prepareStatement(sql).use { stmt ->
                var ix = 1
                for (row in batch) for (value in row) stmt.setObject(ix++, value)
                written += stmt.executeUpdate()
            }
        }
        alignAutoIncrement(conn, table)
        return written
    }

    // Local mixes win per date; prune remote mixes older than our newest
    // (desktop rule — an outdated device can never delete a newer mix).
    private fun pushDailyMix(conn: Connection, db: AppDatabase): Int {
        val mixes = db.dailyMixDao().all()
        if (mixes.isEmpty()) return 0
        val newest = mixes.maxOf { it.mixDate }
        conn.prepareStatement("DELETE FROM daily_mix WHERE mix_date < ?").use {
            it.setString(1, newest)
            it.executeUpdate()
        }
        return upsert(
            conn, "daily_mix", listOf("mix_date", "song_files"),
            "song_files = VALUES(song_files)",
            mixes.map { listOf(it.mixDate, it.songFilesJson) },
        )
    }

    // Append-only history: only rows past the remote MAX(id)
    // (desktop INSERT IGNORE semantics).
    private fun pushHistory(
        conn: Connection, table: String, cols: List<String>, rows: List<List<Any?>>,
    ): Int {
        val last = conn.prepareStatement("SELECT COALESCE(MAX(id), 0) FROM `$table`").use { stmt ->
            stmt.executeQuery().use { rs -> if (rs.next()) rs.getLong(1) else 0L }
        }
        val fresh = rows.filter { (it[0] as Long) > last }
        if (fresh.isEmpty()) return 0
        var written = 0
        for (batch in fresh.chunked(500)) {
            val single = "(${cols.joinToString(", ") { "?" }})"
            val sql = "INSERT IGNORE INTO `$table` (${cols.joinToString(", ") { "`$it`" }}) " +
                "VALUES ${batch.joinToString(", ") { single }}"
            conn.prepareStatement(sql).use { stmt ->
                var ix = 1
                for (row in batch) for (value in row) stmt.setObject(ix++, value)
                written += stmt.executeUpdate()
            }
        }
        if (written > 0) alignAutoIncrement(conn, table)
        return written
    }

    // Desktop _align_auto_increment after explicit-id inserts.
    private fun alignAutoIncrement(conn: Connection, table: String) {
        val next = conn.prepareStatement("SELECT COALESCE(MAX(id), 0) + 1 FROM `$table`").use { stmt ->
            stmt.executeQuery().use { rs -> if (rs.next()) rs.getLong(1) else 0L }
        }
        if (next <= 1) return
        try {
            conn.prepareStatement("ALTER TABLE `$table` AUTO_INCREMENT = $next").use {
                it.executeUpdate()
            }
        } catch (e: Exception) {
            Log.w(TAG, "AUTO_INCREMENT align failed for $table", e)
        }
    }
}
