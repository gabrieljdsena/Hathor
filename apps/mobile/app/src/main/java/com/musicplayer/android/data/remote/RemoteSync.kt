package com.musicplayer.android.data.remote

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.api.RemoteDeletionRow
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncApiException
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.DailyMixEntity
import com.musicplayer.android.data.local.LyricEntity
import com.musicplayer.android.data.local.MusicHistoryEntity
import com.musicplayer.android.data.local.PlaylistEntity
import com.musicplayer.android.data.local.PlaylistHistoryEntity
import com.musicplayer.android.data.local.PodcastChapterEntity
import com.musicplayer.android.data.local.PodcastEntity
import com.musicplayer.android.data.local.PodcastTagEntity
import com.musicplayer.android.data.local.PodcastTagLinkEntity
import com.musicplayer.android.data.local.SongEntity
import com.musicplayer.android.data.local.SongPlaylistEntity
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.delay
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale
import org.json.JSONArray

// Pull merge: server delta -> local Room + missing-file byte downloads
// (desktop api_sync pull, 1:1). One bad row never aborts the pull.
// Server tombstones (deletions-since) ARE applied. The sync cursor persists
// in the "hathor" prefs and advances only on full success.
//
// This writes into data.local (music_player.db) — the same Room database the
// screens read — so a pull is visible immediately. It used to write into a
// separate data.db/hathor.db instance that no UI code ever queried.
object RemoteSync {
    private const val TAG = "RemoteSync"
    private const val CURSOR_KEY = "sync_cursor"

    data class PullReport(val addedSongs: Int, val addedPodcasts: Int, val downloadsOk: Int, val downloadsFailed: Int)

    fun PullReport.summary(): String {
        val total = addedSongs + addedPodcasts
        return "Synced $total new entries from sync server. " +
            "Downloads OK: $downloadsOk, failed: $downloadsFailed."
    }

    suspend fun pullNow(
        db: AppDatabase,
        engine: DownloadEngine,
        context: Context,
        onProgress: suspend (step: String, done: Int, total: Int) -> Unit = { _, _, _ -> }
    ): PullReport {
        if (!SyncConfig.isConfigured()) {
            throw IllegalStateException(SyncConfig.NOT_CONFIGURED)
        }
        onProgress("Reading library from server…", 0, 0)
        val api = SyncApi(SyncConfig.baseUrl(), SyncConfig.token())
        val prefs = context.getSharedPreferences("hathor", Context.MODE_PRIVATE)
        val cursor = prefs.getString(CURSOR_KEY, "") ?: ""
        val delta = try {
            api.getDelta(cursor)
        } catch (e: SyncApiException) {
            throw IllegalStateException(e.message)
        }
        val snapshot = SyncApi.parseSnapshot(delta)
        applyDeletions(db, engine, snapshot.deletions)

        var addedSongs = 0
        var addedPodcasts = 0
        var downloadsOk = 0
        var downloadsFailed = 0
        val total = snapshot.songs.size + snapshot.podcasts.size
        var done = 0

        for (row in snapshot.songs) {
            val existing = db.songDao().getByFile(row.file)
            if (existing == null) {
                db.songDao().insertIgnore(
                    SongEntity(
                        file = row.file,
                        downloadedLink = row.downloadedLink,
                        title = row.title,
                        dateDownload = dateText(row.dateDownloadMillis),
                        artist = row.artist,
                    ),
                )
                addedSongs++
            }
            when (downloadMissing(api, engine.outputDir(), "songs", row.file)) {
                true -> downloadsOk++
                false -> downloadsFailed++
                null -> { /* already on disk */ }
            }
            done++
            if (done % 5 == 0 || done == total) {
                onProgress("Downloading songs", done, total)
            }
        }

        for (row in snapshot.podcasts) {
            val existing = db.podcastDao().getByFile(row.file)
            if (existing == null) {
                db.podcastDao().insertIgnore(
                    PodcastEntity(
                        file = row.file,
                        downloadedLink = row.downloadedLink,
                        title = row.title,
                        dateDownload = dateText(row.dateDownloadMillis),
                        artist = row.artist,
                    ),
                )
                addedPodcasts++
            }
            when (downloadMissing(api, engine.podcastsDir(), "podcasts", row.file)) {
                true -> downloadsOk++
                false -> downloadsFailed++
                null -> { /* already on disk */ }
            }
            done++
            onProgress("Downloading podcasts", done, total)
        }

        onProgress("Updating playlists, lyrics and history…", total, total)

        for (row in snapshot.playlists) {
            val existing = db.playlistDao().getPlaylist(row.id)
            if (existing == null) {
                db.playlistDao().insertPlaylistIgnore(
                    PlaylistEntity(row.id, row.title, row.description, row.thumbnail),
                )
            } else {
                db.playlistDao().updatePlaylist(
                    existing.copy(title = row.title, description = row.description, thumbnail = row.thumbnail),
                )
            }
        }
        for (row in snapshot.songLinks) {
            db.playlistDao().insertSongLinkIgnore(
                SongPlaylistEntity(
                    id = row.id,
                    songFile = row.songFile,
                    playlistId = row.playlistId,
                    dateAdded = dateText(row.dateAddedMillis),
                ),
            )
        }
        for (row in snapshot.lyrics) {
            if (db.playlistDao().insertLyricIgnore(
                    LyricEntity(row.id, row.songFile, row.lyrics, row.offsetMs),
                ) == -1L
            ) {
                val current = db.playlistDao().lyricById(row.id)
                if (current != null) {
                    // Preserve the highlight offset on update (it used to be
                    // dropped here, desyncing nudged songs after every pull).
                    db.playlistDao().updateLyricEntity(
                        current.copy(
                            songFile = row.songFile, lyrics = row.lyrics,
                            offsetMs = row.offsetMs,
                        ),
                    )
                }
            }
        }
        for (row in snapshot.musicHistory) {
            val date = dateText(row.datePlayedMillis)
            // Natural-key dedupe first: a server-rekeyed row must not
            // reappear locally as a second play.
            if (db.playlistDao().musicHistoryIdFor(row.songFile, date) != null) continue
            val inserted = db.playlistDao().insertMusicHistoryIgnore(
                MusicHistoryEntity(row.id, row.songFile, date),
            )
            if (inserted == -1L) {
                // The server id collides with a different local row.
                db.playlistDao().insertMusicHistoryIgnore(
                    MusicHistoryEntity(0, row.songFile, date),
                )
            }
        }
        for (row in snapshot.playlistHistory) {
            db.playlistDao().insertPlaylistHistoryIgnore(
                PlaylistHistoryEntity(row.id, row.playlistId, dateText(row.datePlayedMillis)),
            )
        }
        for (row in snapshot.tags) {
            if (db.podcastTagDao().insertIgnore(PodcastTagEntity(row.id, row.name)) == -1L) {
                val current = db.podcastTagDao().tagById(row.id)
                if (current != null) db.podcastTagDao().updateTag(current.copy(name = row.name))
            }
        }
        for (row in snapshot.tagLinks) {
            db.podcastTagLinkDao().insertLinkIgnore(
                PodcastTagLinkEntity(row.id, row.podcastFile, row.tagId),
            )
            // The server's assignment supersedes a local unassign tombstone.
            db.syncDeletionDao().delete(
                "podcast_tag_links", tagLinkKey(row.tagId, row.podcastFile),
            )
        }
        // Episode chapters (upsert by id; empty delta never wipes local).
        for (row in snapshot.chapters) {
            if (db.podcastChapterDao().insertIgnore(
                    PodcastChapterEntity(
                        row.id, row.podcastFile, row.name, row.startSecs, row.endSecs,
                    ),
                ) == -1L
            ) {
                val current = db.podcastChapterDao().byId(row.id)
                if (current != null) {
                    db.podcastChapterDao().update(
                        current.copy(
                            podcastFile = row.podcastFile, name = row.name,
                            startSecs = row.startSecs, endSecs = row.endSecs,
                        ),
                    )
                }
            }
        }
        // Adopt the remote daily mix (same mix of the day on every device),
        // then prune anything older than today (desktop rule).
        if (snapshot.mixes.isNotEmpty()) {
            for (mix in snapshot.mixes) {
                try {
                    // Validate the JSON shape; malformed rows are skipped.
                    JSONArray(mix.songFilesJson)
                    db.dailyMixDao().save(DailyMixEntity(mix.mixDate, mix.songFilesJson))
                } catch (_: Exception) {
                    continue
                }
            }
            db.dailyMixDao().purgeNonToday(LocalDate.now().toString())
        }

        // Cursor advances only on full success. A failed download must retry
        // the same delta on the next pull instead of silently skipping files.
        val next = delta.optString("cursor")
        if (next.isNotBlank() && downloadsFailed == 0) {
            prefs.edit().putString(CURSOR_KEY, next).apply()
        } else if (downloadsFailed > 0) {
            Log.w(TAG, "Not advancing sync cursor: $downloadsFailed download(s) failed")
        }
        Log.i(
            TAG,
            "Pull complete: songs=$addedSongs podcasts=$addedPodcasts " +
                "downloadsOk=$downloadsOk failed=$downloadsFailed"
        )
        return PullReport(addedSongs, addedPodcasts, downloadsOk, downloadsFailed)
    }

    /** Force the next pull to replay the full delta (retries missing files). */
    fun resetCursor(context: Context) {
        context.getSharedPreferences("hathor", Context.MODE_PRIVATE)
            .edit()
            .remove(CURSOR_KEY)
            .apply()
    }

    // Podcast tag-link tombstone key: "<tagId>:<file>". Split on the first
    // colon so a filename containing colons still round-trips.
    internal fun parseTagLinkKey(key: String): Pair<Long, String>? {
        val sep = key.indexOf(':')
        if (sep <= 0 || sep == key.lastIndex) return null
        val tagId = key.substring(0, sep).toLongOrNull() ?: return null
        return tagId to key.substring(sep + 1)
    }

    private fun tagLinkKey(tagId: Long, file: String) = "$tagId:$file"

    // Server-authoritative tombstones (delta lists deletions-since): drop
    // the local rows AND the bytes, so deletes converge instead of
    // resurrecting on the next push. One bad tombstone never aborts.
    private suspend fun applyDeletions(
        db: AppDatabase,
        engine: DownloadEngine,
        deletions: List<RemoteDeletionRow>,
    ) {
        for (d in deletions) {
            if (d.rowKey.isBlank()) continue
            try {
                when (d.tableName.lowercase()) {
                    "songs" -> {
                        db.songDao().deleteSong(d.rowKey)
                        db.playlistDao().deleteLinksForSong(d.rowKey)
                        db.playlistDao().deleteLyricsForSong(d.rowKey)
                        db.playlistDao().deleteMusicHistoryForSong(d.rowKey)
                        deleteDiskFile(engine.outputDir(), d.rowKey)
                    }
                    "podcasts" -> {
                        db.podcastDao().deleteByFile(d.rowKey)
                        db.podcastTagLinkDao().deleteLinksForFile(d.rowKey)
                        db.podcastChapterDao().deleteForEpisode(d.rowKey)
                        deleteDiskFile(engine.podcastsDir(), d.rowKey)
                    }
                    "playlists" -> d.rowKey.toLongOrNull()?.let { id ->
                        db.playlistDao().deleteSongLinksForPlaylists(listOf(id))
                        db.playlistDao().deletePlaylistHistory(id)
                        db.playlistDao().deletePlaylistById(id)
                    }
                    "lyrics" -> db.playlistDao().deleteLyricsForSong(d.rowKey)
                    "music_history" -> db.playlistDao().deleteMusicHistoryForSong(d.rowKey)
                    "playlist_history" -> d.rowKey.toLongOrNull()?.let {
                        db.playlistDao().deletePlaylistHistory(it)
                    }
                    "podcast_tags" -> d.rowKey.toLongOrNull()?.let { id ->
                        db.podcastTagDao().deleteLinksForTags(listOf(id))
                        db.podcastTagDao().deleteTagById(id)
                    }
                    "podcast_tag_links" -> parseTagLinkKey(d.rowKey)?.let { (tagId, file) ->
                        db.podcastTagLinkDao().deleteLink(file, tagId)
                    }
                    "podcast_chapters" -> db.podcastChapterDao().deleteForEpisode(d.rowKey)
                }
            } catch (e: Exception) {
                Log.w(TAG, "Could not apply deletion ${d.tableName}/${d.rowKey}", e)
            }
        }
    }

    private fun deleteDiskFile(dir: File, name: String) {
        if ('/' in name || '\\' in name) return
        try {
            val f = dir.resolve(name)
            if (f.exists()) f.delete()
        } catch (e: Exception) {
            Log.w(TAG, "Could not remove deleted file $name", e)
        }
    }

    // Byte-identical server download under the exact filename (replaces the
    // old YouTube re-download — no codec drift, no wrong search hits).
    // Null = already on disk (not counted); false = failed (counted).
    private suspend fun downloadMissing(
        api: SyncApi,
        outDir: File,
        library: String,
        targetFile: String,
    ): Boolean? {
        if (targetFile.isBlank() || '/' in targetFile || '\\' in targetFile) return false
        if (outDir.resolve(targetFile).exists()) return null
        var lastError: Exception? = null
        for (attempt in 1..3) {
            try {
                if (api.downloadFile(targetFile, library, outDir.resolve(targetFile))) {
                    return true
                }
                lastError = IllegalStateException("download returned false")
            } catch (e: SyncApiException) {
                lastError = e
                // 401/404 will not improve on retry; the cursor stays put so
                // the next full pull still reports them.
                val message = e.message.orEmpty()
                if (message.contains("401") || message.contains("404")) break
            } catch (e: Exception) {
                lastError = e
            }
            if (attempt < 3) delay(500L * attempt)
        }
        Log.w(
            TAG,
            "Pull download failed for $targetFile: ${lastError?.message ?: "unknown"}"
        )
        return false
    }

    // Server ISO timestamps -> the desktop text shape the local DB and UI
    // already use ("yyyy-MM-dd HH:mm:ss", UTC). Pull used to store raw ISO,
    // which PodcastsScreen.shortDate could not parse and re-pushed verbatim.
    private val STAMP = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss", Locale.US)

    private fun dateText(millis: Long): String =
        LocalDateTime.ofInstant(Instant.ofEpochMilli(millis), ZoneOffset.UTC).format(STAMP)
}
