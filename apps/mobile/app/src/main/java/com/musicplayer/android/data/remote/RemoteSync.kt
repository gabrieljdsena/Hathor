package com.musicplayer.android.data.remote

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.api.RemoteDeletionRow
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncApiException
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.db.DailyMixEntity
import com.musicplayer.android.data.db.LyricEntity
import com.musicplayer.android.data.db.MusicHistoryEntry
import com.musicplayer.android.data.db.PlaylistEntity
import com.musicplayer.android.data.db.PlaylistHistoryEntry
import com.musicplayer.android.data.db.PodcastChapterEntity
import com.musicplayer.android.data.db.PodcastEntity
import com.musicplayer.android.data.db.PodcastTagEntity
import com.musicplayer.android.data.db.PodcastTagLink
import com.musicplayer.android.data.db.SongEntity
import com.musicplayer.android.data.db.SongPlaylistLink
import com.musicplayer.android.engine.DownloadEngine
import java.io.File
import java.time.LocalDate
import org.json.JSONArray

// Pull merge: server delta -> local Room + missing-file byte downloads
// (desktop api_sync pull, 1:1). One bad row never aborts the pull.
// Server tombstones (deletions-since) ARE applied — unlike the retired
// JDBC pull, a delta must converge deletes or they resurrect. The sync
// cursor persists in the "hathor" prefs and advances only on full success.
object RemoteSync {
    private const val TAG = "RemoteSync"
    private const val CURSOR_KEY = "sync_cursor"

    data class PullReport(val addedSongs: Int, val addedPodcasts: Int, val downloadsOk: Int, val downloadsFailed: Int)

    fun PullReport.summary(): String {
        val total = addedSongs + addedPodcasts
        return "Synced $total new entries from sync server. " +
            "Downloads OK: $downloadsOk, failed: $downloadsFailed."
    }

    suspend fun pullNow(db: AppDatabase, engine: DownloadEngine, context: Context): PullReport {
        if (!SyncConfig.isConfigured()) {
            throw IllegalStateException(SyncConfig.NOT_CONFIGURED)
        }
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

        for (row in snapshot.songs) {
            val existing = db.songDao().byFile(row.file)
            if (existing == null) {
                db.songDao().insertIgnore(
                    SongEntity(row.file, row.downloadedLink, row.title, row.dateDownloadMillis, row.artist),
                )
                addedSongs++
            }
            when (downloadMissing(api, engine.outputDir(), "songs", row.file)) {
                true -> downloadsOk++
                false -> downloadsFailed++
                null -> { /* already on disk */ }
            }
        }

        for (row in snapshot.podcasts) {
            val existing = db.podcastDao().byFile(row.file)
            if (existing == null) {
                db.podcastDao().insertIgnore(
                    PodcastEntity(row.file, row.downloadedLink, row.title, row.dateDownloadMillis, row.artist),
                )
                addedPodcasts++
            }
            when (downloadMissing(api, engine.podcastsDir(), "podcasts", row.file)) {
                true -> downloadsOk++
                false -> downloadsFailed++
                null -> { /* already on disk */ }
            }
        }

        for (row in snapshot.playlists) {
            val existing = db.playlistDao().byId(row.id)
            if (existing == null) {
                db.playlistDao().insertIgnore(
                    PlaylistEntity(row.id, row.title, row.description, row.thumbnail),
                )
            } else {
                db.playlistDao().update(
                    existing.copy(title = row.title, description = row.description, thumbnail = row.thumbnail),
                )
            }
        }
        for (row in snapshot.songLinks) {
            if (db.songPlaylistDao().insertIgnore(
                    SongPlaylistLink(row.id, row.songFile, row.playlistId, row.dateAddedMillis),
                ) == -1L
            ) {
                // Row exists under this id: scoped replace happens on push;
                // pull only fills gaps, never rewrites links.
            }
        }
        for (row in snapshot.lyrics) {
            if (db.lyricDao().insertIgnore(
                    LyricEntity(row.id, row.songFile, row.lyrics, row.offsetMs),
                ) == -1L
            ) {
                val current = db.lyricDao().byId(row.id)
                if (current != null) {
                    // Preserve the highlight offset on update (it used to be
                    // dropped here, desyncing nudged songs after every pull).
                    db.lyricDao().update(
                        current.copy(
                            songFile = row.songFile, lyrics = row.lyrics,
                            offsetMs = row.offsetMs,
                        ),
                    )
                }
            }
        }
        for (row in snapshot.musicHistory) {
            if (db.musicHistoryDao().insertIgnore(
                    MusicHistoryEntry(row.id, row.songFile, row.datePlayedMillis),
                ) == -1L
            ) {
                // History rows are append-only; id collision = already have it.
            }
        }
        for (row in snapshot.playlistHistory) {
            if (db.playlistHistoryDao().insertIgnore(
                    PlaylistHistoryEntry(row.id, row.playlistId, row.datePlayedMillis),
                ) == -1L
            ) {
                // Same append-only rule as music history.
            }
        }
        for (row in snapshot.tags) {
            if (db.podcastTagDao().insertIgnore(PodcastTagEntity(row.id, row.name)) == -1L) {
                val current = db.podcastTagDao().byId(row.id)
                if (current != null) db.podcastTagDao().update(current.copy(name = row.name))
            }
        }
        for (row in snapshot.tagLinks) {
            db.podcastTagLinkDao().insertIgnore(
                PodcastTagLink(row.id, row.podcastFile, row.tagId),
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
                    db.dailyMixDao().replace(DailyMixEntity(mix.mixDate, mix.songFilesJson))
                } catch (_: Exception) {
                    continue
                }
            }
            db.dailyMixDao().pruneBefore(LocalDate.now().toString())
        }

        // Cursor advances only on full success (one bad row never aborts,
        // but a failed pull must retry the same delta next time).
        val next = delta.optString("cursor")
        if (next.isNotBlank()) prefs.edit().putString(CURSOR_KEY, next).apply()
        return PullReport(addedSongs, addedPodcasts, downloadsOk, downloadsFailed)
    }

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
                        db.songDao().deleteByFile(d.rowKey)
                        db.songPlaylistDao().deleteByFile(d.rowKey)
                        db.lyricDao().deleteByFile(d.rowKey)
                        db.musicHistoryDao().deleteByFile(d.rowKey)
                        deleteDiskFile(engine.outputDir(), d.rowKey)
                    }
                    "podcasts" -> {
                        db.podcastDao().deleteByFile(d.rowKey)
                        db.podcastTagLinkDao().deleteForEpisode(d.rowKey)
                        db.podcastChapterDao().deleteForEpisode(d.rowKey)
                        deleteDiskFile(engine.podcastsDir(), d.rowKey)
                    }
                    "playlists" -> d.rowKey.toLongOrNull()?.let { id ->
                        db.songPlaylistDao().deleteForPlaylists(listOf(id))
                        db.playlistHistoryDao().deleteByPlaylistId(id)
                        db.playlistDao().deleteById(id)
                    }
                    "lyrics" -> db.lyricDao().deleteByFile(d.rowKey)
                    "music_history" -> db.musicHistoryDao().deleteByFile(d.rowKey)
                    "playlist_history" -> d.rowKey.toLongOrNull()?.let {
                        db.playlistHistoryDao().deleteByPlaylistId(it)
                    }
                    "podcast_tags" -> d.rowKey.toLongOrNull()?.let { id ->
                        db.podcastTagLinkDao().deleteForTags(listOf(id))
                        db.podcastTagDao().deleteById(id)
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
    private fun downloadMissing(
        api: SyncApi,
        outDir: File,
        library: String,
        targetFile: String,
    ): Boolean? {
        if (targetFile.isBlank() || '/' in targetFile || '\\' in targetFile) return false
        if (outDir.resolve(targetFile).exists()) return null
        return try {
            if (api.downloadFile(targetFile, library, outDir.resolve(targetFile))) true else false
        } catch (e: SyncApiException) {
            Log.w(TAG, "Pull download failed for $targetFile: ${e.message}")
            false
        } catch (e: Exception) {
            Log.w(TAG, "Pull download failed for $targetFile", e)
            false
        }
    }
}
