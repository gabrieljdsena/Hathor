package com.musicplayer.android.data

import android.content.Context
import com.musicplayer.android.data.local.MusicHistoryEntity
import com.musicplayer.android.data.local.AppDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * Port of get_download_history() / get_played_history().
 *
 * Download history (desktop): Songs with a non-empty downloaded_link whose
 * file still exists, newest first. Played history: Music_History newest
 * first, existing files only. No paging yet (desktop pages at 10; the list
 * is small on v1 — paging lands with the download-manager phase if needed).
 */
data class HistoryItem(
    val file: String,
    val title: String,
    val artist: String,
    val date: String?,
    val link: String? = null
)

class HistoryRepository(context: Context, private val metadata: MetadataRepository) {

    private val appContext = context.applicationContext
    private val db = AppDatabase.get(appContext)

    suspend fun downloaded(musicDir: File): List<HistoryItem> = withContext(Dispatchers.IO) {
        db.songDao().getDownloaded().mapNotNull { row ->
            if (!File(musicDir, row.file).exists()) return@mapNotNull null
            // Cache-first: avoids a MediaMetadataRetriever open per row.
            val meta = metadata.metaFor(musicDir, row.file)
            HistoryItem(row.file, meta.title, meta.artist, row.dateDownload, row.downloadedLink)
        }
    }

    suspend fun played(musicDir: File): List<HistoryItem> = withContext(Dispatchers.IO) {
        db.playlistDao().getMusicHistory().mapNotNull { row ->
            if (!File(musicDir, row.songFile).exists()) return@mapNotNull null
            val meta = metadata.metaFor(musicDir, row.songFile)
            HistoryItem(row.songFile, meta.title, meta.artist, row.datePlayed)
        }
    }

    /** Called when playback starts a new file (desktop logs Music_History on play). */
    suspend fun recordPlayed(file: String) = withContext(Dispatchers.IO) {
        try {
            db.playlistDao().insertMusicHistory(
                MusicHistoryEntity(songFile = file, datePlayed = STAMP.format(Date()))
            )
        } catch (_: Exception) {
        }
    }

    companion object {
        private val STAMP = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }
    }
}
