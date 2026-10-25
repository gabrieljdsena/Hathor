package com.musicplayer.android.data.remote

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.local.AppDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.sql.Connection

/**
 * Upload direction: local SQLite -> remote MySQL/TiDB.
 * Faithful port of sync.py DatabaseSync._run_sync() + helpers.
 *
 * Push cycle order (spec): tombstones -> songs -> podcasts -> playlists
 * (explicit ids + AUTO_INCREMENT fix) -> song<->playlist full replace ->
 * podcast tags (explicit ids + AUTO_INCREMENT fix) -> podcast tag links
 * full replace -> lyrics -> daily mix (last writer wins; only remote mixes
 * OLDER than the newest local one are deleted) -> incremental history append.
 * Manual-only: triggered from Settings Push, never on a timer.
 */
class RemoteUpload(private val context: Context, private val config: RemoteConfig = RemoteConfig()) {

    suspend fun run(): String = withContext(Dispatchers.IO) {
        if (!config.isConfigured) {
            throw IllegalStateException("No remote DB configured. Remote sync unavailable.")
        }
        val local = AppDatabase.get(context.applicationContext)
        try {
            RemoteConn.connect(config).use { remote ->
                initSchema(remote)
                val tombstones = applyDeletions(local, remote)
                val songs = syncSongs(local, remote)
                val podcasts = syncPodcasts(local, remote)
                val playlists = syncPlaylists(local, remote)
                syncSongPlaylist(local, remote)
                val tags = syncPodcastTags(local, remote)
                syncPodcastTagLinks(local, remote)
                val lyrics = syncLyrics(local, remote)
                val mixes = syncDailyMix(local, remote)
                val hist = syncHistory(local, remote, "Music_History", "music_history",
                    listOf("id", "song_file", "date_played"))
                val phist = syncHistory(local, remote, "Playlist_History", "playlist_history",
                    listOf("id", "playlist_id", "date_played"))
                "Push complete: $songs songs, $podcasts podcasts, $playlists playlists, " +
                    "$tags podcast tags, $lyrics lyrics, $mixes mixes, $hist+$phist history rows, $tombstones deletions."
            }
        } catch (e: IllegalStateException) {
            throw e
        } catch (e: Exception) {
            Log.e(TAG, "Push failed", e)
            throw IllegalStateException("Error pushing to remote DB: ${e.message}", e)
        }
    }

    /** Same REMOTE_SCHEMA as sync.py (Settings stays local-only). */
    private fun initSchema(remote: Connection) {
        val ddl = listOf(
            "CREATE TABLE IF NOT EXISTS songs (file VARCHAR(255) PRIMARY KEY, downloaded_link VARCHAR(255), title VARCHAR(255) NOT NULL, date_download TIMESTAMP DEFAULT CURRENT_TIMESTAMP, artist VARCHAR(255))",
            "CREATE TABLE IF NOT EXISTS podcasts (file VARCHAR(255) PRIMARY KEY, downloaded_link VARCHAR(255), title VARCHAR(255) NOT NULL, date_download TIMESTAMP DEFAULT CURRENT_TIMESTAMP, artist VARCHAR(255))",
            "CREATE TABLE IF NOT EXISTS playlists (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, title VARCHAR(255) NOT NULL, description TEXT, thumbnail TEXT)",
            "CREATE TABLE IF NOT EXISTS song_playlist (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, song_file VARCHAR(255) NOT NULL, playlist_id BIGINT NOT NULL, date_added TIMESTAMP DEFAULT CURRENT_TIMESTAMP)",
            "CREATE TABLE IF NOT EXISTS lyrics (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, song_file VARCHAR(255) NOT NULL, lyrics TEXT)",
            "CREATE TABLE IF NOT EXISTS music_history (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, song_file VARCHAR(255) NOT NULL, date_played TIMESTAMP DEFAULT CURRENT_TIMESTAMP)",
            "CREATE TABLE IF NOT EXISTS playlist_history (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, playlist_id BIGINT NOT NULL, date_played TIMESTAMP DEFAULT CURRENT_TIMESTAMP)",
            "CREATE TABLE IF NOT EXISTS daily_mix (mix_date VARCHAR(10) PRIMARY KEY, song_files TEXT NOT NULL, created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)",
            "CREATE TABLE IF NOT EXISTS podcast_tags (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, name VARCHAR(255) NOT NULL UNIQUE)",
            "CREATE TABLE IF NOT EXISTS podcast_tag_links (id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, podcast_file VARCHAR(255) NOT NULL, tag_id BIGINT NOT NULL)"
        )
        remote.createStatement().use { st -> ddl.forEach { st.execute(it) } }
        remote.commit()
        // Migrate remotes created before the thumbnail column existed.
        try {
            remote.createStatement().use { it.execute("ALTER TABLE playlists ADD COLUMN thumbnail TEXT") }
            remote.commit()
        } catch (_: Exception) {
            try {
                remote.rollback()
            } catch (_: Exception) {
            }
        }
    }

    /** Desktop _apply_deletions(): tombstones -> remote DELETEs; cleared only on success. */
    private fun applyDeletions(local: AppDatabase, remote: Connection): Int {
        val cols = mapOf(
            "songs" to "file",
            "podcasts" to "file",
            "playlists" to "id",
            "lyrics" to "song_file",
            "music_history" to "song_file",
            "playlist_history" to "playlist_id",
            "podcast_tags" to "id"
        )
        // Sync_Deletions has no DAO (write-only log); read via the helper.
        val rows = mutableListOf<Pair<String, String>>()
        local.openHelper.readableDatabase
            .query("SELECT table_name, row_key FROM Sync_Deletions").use { c ->
                while (c.moveToNext()) rows.add(c.getString(0) to c.getString(1))
            }
        if (rows.isEmpty()) return 0
        for ((table, key) in rows) {
            val col = cols[table] ?: continue
            remote.prepareStatement("DELETE FROM $table WHERE $col = ?").use { ps ->
                ps.setString(1, key)
                ps.executeUpdate()
            }
        }
        remote.commit()
        local.openHelper.writableDatabase.execSQL("DELETE FROM Sync_Deletions")
        return rows.size
    }

    private fun syncSongs(local: AppDatabase, remote: Connection): Int {
        // DAO read is suspend; bridge with runBlocking (we're already on IO).
        val rows = kotlinx.coroutines.runBlocking { local.songDao().getAll() }
        if (rows.isEmpty()) return 0
        remote.prepareStatement(
            "INSERT INTO songs (file, downloaded_link, title, date_download, artist) VALUES (?, ?, ?, ?, ?) " +
                "ON DUPLICATE KEY UPDATE downloaded_link = VALUES(downloaded_link), title = VALUES(title), " +
                "date_download = VALUES(date_download), artist = VALUES(artist)"
        ).use { ps ->
            for (r in rows) {
                ps.setString(1, r.file)
                ps.setString(2, r.downloadedLink)
                ps.setString(3, r.title)
                ps.setString(4, r.dateDownload)
                ps.setString(5, r.artist)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        return rows.size
    }

    private fun syncPodcasts(local: AppDatabase, remote: Connection): Int {
        val rows = kotlinx.coroutines.runBlocking { local.podcastDao().getAll() }
        if (rows.isEmpty()) return 0
        remote.prepareStatement(
            "INSERT INTO podcasts (file, downloaded_link, title, date_download, artist) VALUES (?, ?, ?, ?, ?) " +
                "ON DUPLICATE KEY UPDATE downloaded_link = VALUES(downloaded_link), title = VALUES(title), " +
                "date_download = VALUES(date_download), artist = VALUES(artist)"
        ).use { ps ->
            for (r in rows) {
                ps.setString(1, r.file)
                ps.setString(2, r.downloadedLink)
                ps.setString(3, r.title)
                ps.setString(4, r.dateDownload)
                ps.setString(5, r.artist)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        return rows.size
    }

    private fun syncPlaylists(local: AppDatabase, remote: Connection): Int {
        val rows = kotlinx.coroutines.runBlocking { local.playlistDao().getPlaylists() }
        if (rows.isEmpty()) return 0
        remote.prepareStatement(
            "INSERT INTO playlists (id, title, description, thumbnail) VALUES (?, ?, ?, ?) " +
                "ON DUPLICATE KEY UPDATE title = VALUES(title), description = VALUES(description), thumbnail = VALUES(thumbnail)"
        ).use { ps ->
            for (r in rows) {
                ps.setLong(1, r.id)
                ps.setString(2, r.title)
                ps.setString(3, r.description)
                ps.setString(4, r.thumbnail)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        alignAutoIncrement(remote, "playlists")
        return rows.size
    }

    private fun syncSongPlaylist(local: AppDatabase, remote: Connection) {
        // Full replace, like desktop _sync_song_playlist().
        data class Link(val id: Long, val file: String, val pid: Long)
        val playlists = kotlinx.coroutines.runBlocking { local.playlistDao().getPlaylists() }
        val rows = mutableListOf<Link>()
        for (pl in playlists) {
            val links = kotlinx.coroutines.runBlocking { local.playlistDao().getLinks(pl.id) }
            links.forEach { rows.add(Link(it.id, it.songFile, it.playlistId)) }
        }
        remote.createStatement().use { it.execute("DELETE FROM song_playlist") }
        if (rows.isNotEmpty()) {
            remote.prepareStatement(
                "INSERT INTO song_playlist (id, song_file, playlist_id) VALUES (?, ?, ?)"
            ).use { ps ->
                for (l in rows) {
                    ps.setLong(1, l.id)
                    ps.setString(2, l.file)
                    ps.setLong(3, l.pid)
                    ps.addBatch()
                }
                ps.executeBatch()
            }
        }
        remote.commit()
        alignAutoIncrement(remote, "song_playlist")
    }

    /**
     * Podcast tags push (same shape as playlists: explicit ids so both
     * sides stay stable, then AUTO_INCREMENT realigned).
     */
    private fun syncPodcastTags(local: AppDatabase, remote: Connection): Int {
        val rows = kotlinx.coroutines.runBlocking { local.podcastTagDao().getTags() }
        if (rows.isEmpty()) return 0
        remote.prepareStatement(
            "INSERT INTO podcast_tags (id, name) VALUES (?, ?) " +
                "ON DUPLICATE KEY UPDATE name = VALUES(name)"
        ).use { ps ->
            for (r in rows) {
                ps.setLong(1, r.id)
                ps.setString(2, r.name)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        alignAutoIncrement(remote, "podcast_tags")
        return rows.size
    }

    /** Podcast tag links push: full replace, like _sync_song_playlist(). */
    private fun syncPodcastTagLinks(local: AppDatabase, remote: Connection) {
        val rows = kotlinx.coroutines.runBlocking { local.podcastTagDao().getLinks() }
        remote.createStatement().use { it.execute("DELETE FROM podcast_tag_links") }
        if (rows.isNotEmpty()) {
            remote.prepareStatement(
                "INSERT INTO podcast_tag_links (id, podcast_file, tag_id) VALUES (?, ?, ?)"
            ).use { ps ->
                for (l in rows) {
                    ps.setLong(1, l.id)
                    ps.setString(2, l.podcastFile)
                    ps.setLong(3, l.tagId)
                    ps.addBatch()
                }
                ps.executeBatch()
            }
        }
        remote.commit()
        alignAutoIncrement(remote, "podcast_tag_links")
    }

    private fun syncLyrics(local: AppDatabase, remote: Connection): Int {
        val rows = mutableListOf<Triple<Long, String, String?>>()
        local.openHelper.readableDatabase.query("SELECT id, song_file, lyrics FROM Lyrics").use { c ->
            while (c.moveToNext()) rows.add(Triple(c.getLong(0), c.getString(1), c.getString(2)))
        }
        if (rows.isEmpty()) return 0
        remote.prepareStatement(
            "INSERT INTO lyrics (id, song_file, lyrics) VALUES (?, ?, ?) " +
                "ON DUPLICATE KEY UPDATE song_file = VALUES(song_file), lyrics = VALUES(lyrics)"
        ).use { ps ->
            for ((id, file, text) in rows) {
                ps.setLong(1, id)
                ps.setString(2, file)
                ps.setString(3, text)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        alignAutoIncrement(remote, "lyrics")
        return rows.size
    }

    /**
     * Desktop _sync_daily_mix(): last writer wins per date; only remote mixes
     * OLDER than the newest local one are deleted, so an outdated device can
     * never delete a newer mix pushed by another device.
     */
    private fun syncDailyMix(local: AppDatabase, remote: Connection): Int {
        val rows = kotlinx.coroutines.runBlocking { local.dailyMixDao().getAll() }
        if (rows.isEmpty()) return 0
        val newest = rows.maxOf { it.mixDate }
        try {
            remote.prepareStatement("DELETE FROM daily_mix WHERE mix_date < ?").use { ps ->
                ps.setString(1, newest)
                ps.executeUpdate()
            }
        } catch (_: Exception) {
            // daily_mix may not exist on very old remotes; initSchema covers
            // it, so a failure here is non-fatal.
        }
        remote.prepareStatement(
            "INSERT INTO daily_mix (mix_date, song_files) VALUES (?, ?) " +
                "ON DUPLICATE KEY UPDATE song_files = VALUES(song_files)"
        ).use { ps ->
            for (r in rows) {
                ps.setString(1, r.mixDate)
                ps.setString(2, r.songFiles)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        return rows.size
    }

    /**
     * Desktop _sync_history(): incremental append-only sync by max remote id.
     * localTable is the Room table name; remoteTable the MySQL one.
     */
    private fun syncHistory(
        local: AppDatabase,
        remote: Connection,
        localTable: String,
        remoteTable: String,
        columns: List<String>
    ): Int {
        val lastSynced = remote.createStatement().use { st ->
            st.executeQuery("SELECT COALESCE(MAX(id), 0) FROM $remoteTable").use { rs ->
                rs.next()
                rs.getLong(1)
            }
        }
        // Song_Playlist-style tables carry dates as TEXT; read generically.
        val keyCol = columns[1]
        val dateCol = columns[2]
        val pending = mutableListOf<Triple<Long, String, String?>>()
        local.openHelper.readableDatabase.query(
            "SELECT id, $keyCol, $dateCol FROM $localTable WHERE id > $lastSynced ORDER BY id"
        ).use { c ->
            while (c.moveToNext()) pending.add(Triple(c.getLong(0), c.getString(1), c.getString(2)))
        }
        if (pending.isEmpty()) return 0
        // playlist_id is numeric; key column value may be non-numeric for song_file.
        val numericKey = keyCol == "playlist_id"
        remote.prepareStatement(
            "INSERT IGNORE INTO $remoteTable (${columns.joinToString(", ")}) VALUES (?, ?, ?)"
        ).use { ps ->
            for ((id, key, date) in pending) {
                ps.setLong(1, id)
                if (numericKey) ps.setLong(2, key.toLongOrNull() ?: continue) else ps.setString(2, key)
                ps.setString(3, date)
                ps.addBatch()
            }
            ps.executeBatch()
        }
        remote.commit()
        alignAutoIncrement(remote, remoteTable)
        return pending.size
    }

    private fun alignAutoIncrement(remote: Connection, table: String) {
        val next = remote.createStatement().use { st ->
            st.executeQuery("SELECT COALESCE(MAX(id), 0) + 1 FROM $table").use { rs ->
                rs.next()
                rs.getLong(1)
            }
        }
        if (next > 1) {
            remote.createStatement().use { it.execute("ALTER TABLE $table AUTO_INCREMENT = $next") }
            remote.commit()
        }
    }

    companion object {
        private const val TAG = "RemoteUpload"
    }
}
