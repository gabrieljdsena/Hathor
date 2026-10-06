package com.musicplayer.android.data.remote

import android.util.Log
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

// Pull merge: remote snapshot -> local Room + missing-file downloads
// (desktop sync_remote_to_local_and_download, 1:1). One bad row never
// aborts the pull. Tombstones are NOT applied here (desktop parity —
// deletions propagate through push; the rows are already gone remotely).
object RemoteSync {
    private const val TAG = "RemoteSync"

    data class PullReport(val addedSongs: Int, val addedPodcasts: Int, val downloadsOk: Int, val downloadsFailed: Int)

    fun PullReport.summary(): String {
        val total = addedSongs + addedPodcasts
        return "Synced $total new entries from remote DB. " +
            "Downloads OK: $downloadsOk, failed: $downloadsFailed."
    }

    suspend fun pullNow(db: AppDatabase, engine: DownloadEngine): PullReport {
        if (!RemoteDb.isConfigured()) {
            throw IllegalStateException("No remote DB configured. Remote sync unavailable.")
        }
        val conn = RemoteDb.open()
        val snapshot = try {
            RemoteReader.readAll(conn)
        } finally {
            try { conn.close() } catch (_: Exception) { }
        }

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
            if (!engine.outputDir().resolve(row.file).exists()) {
                if (downloadMissing(engine, engine.outputDir(), row.downloadedLink, row.title, row.artist, row.file)) {
                    downloadsOk++
                } else {
                    downloadsFailed++
                }
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
            if (!engine.podcastsDir().resolve(row.file).exists()) {
                if (downloadMissing(engine, engine.podcastsDir(), row.downloadedLink, row.title, row.artist, row.file)) {
                    downloadsOk++
                } else {
                    downloadsFailed++
                }
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
        // Episode chapters (upsert by id; empty snapshot never wipes local).
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

        return PullReport(addedSongs, addedPodcasts, downloadsOk, downloadsFailed)
    }

    // Pin the exact remote filename on disk so the merged row resolves
    // instead of dangling (desktop target_file parity).
    private suspend fun downloadMissing(
        engine: DownloadEngine,
        outDir: File,
        downloadedLink: String?,
        title: String,
        artist: String?,
        targetFile: String,
    ): Boolean {
        return try {
            val url = if (!downloadedLink.isNullOrBlank()) {
                downloadedLink
            } else {
                "$title ${artist.orEmpty()} audio".trim()
            }
            val produced = engine.downloadToMp3(url, outDir = outDir).getOrElse {
                Log.w(TAG, "Pull download failed for $targetFile", it)
                return false
            }
            val target = outDir.resolve(targetFile)
            if (produced.absolutePath == target.absolutePath) return true
            if (target.exists() && !target.delete()) {
                Log.w(TAG, "Pull download: could not replace $targetFile")
                return false
            }
            if (!produced.renameTo(target)) {
                Log.w(TAG, "Pull download: could not rename to $targetFile")
                return false
            }
            true
        } catch (e: Exception) {
            Log.w(TAG, "Pull download failed for $targetFile", e)
            false
        }
    }
}
