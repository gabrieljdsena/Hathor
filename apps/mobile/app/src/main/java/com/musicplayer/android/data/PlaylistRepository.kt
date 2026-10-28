package com.musicplayer.android.data

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.PlaylistEntity
import com.musicplayer.android.data.local.SongEntity
import com.musicplayer.android.data.local.SongPlaylistEntity
import com.musicplayer.android.data.local.SyncDeletionEntity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * Port of the desktop playlist API (DatabaseManager.load_playlists /
 * new_playlist / update_playlist / delete_playlist / get_playlist_songs /
 * get_song_playlists / update_song_playlists).
 */
data class PlaylistItem(
    val id: Long,
    val title: String,
    val description: String?,
    val thumbnail: String?,
    val songCount: Int
)

class PlaylistRepository(context: Context, private val metadata: MetadataRepository) {

    private val appContext = context.applicationContext
    private val db = AppDatabase.get(appContext)

    private val _playlists = MutableStateFlow<List<PlaylistItem>>(emptyList())
    val playlists: StateFlow<List<PlaylistItem>> = _playlists.asStateFlow()

    suspend fun refresh() = withContext(Dispatchers.IO) {
        val pl = db.playlistDao()
        _playlists.value = pl.getPlaylists().map {
            PlaylistItem(it.id, it.title, it.description, it.thumbnail, pl.countLinks(it.id))
        }
    }

    suspend fun create(title: String, description: String?): Long = withContext(Dispatchers.IO) {
        val id = db.playlistDao().insertPlaylist(PlaylistEntity(title = title, description = description, thumbnail = null))
        refresh()
        id
    }

    suspend fun rename(id: Long, title: String, description: String?) = withContext(Dispatchers.IO) {
        db.playlistDao().renamePlaylist(id, title, description)
        refresh()
    }

    suspend fun setThumbnail(id: Long, dataUri: String?) = withContext(Dispatchers.IO) {
        db.playlistDao().setThumbnail(id, dataUri)
        refresh()
    }

    /** Desktop delete_playlist(): links + playlist history + row + tombstones. */
    suspend fun delete(id: Long): Boolean = withContext(Dispatchers.IO) {
        try {
            val pl = db.playlistDao()
            pl.deletePlaylistHistory(id)
            pl.deletePlaylistLinks(id)
            pl.deletePlaylist(id)
            db.songDao().recordDeletion(SyncDeletionEntity(tableName = "playlists", rowKey = id.toString()))
            db.songDao().recordDeletion(SyncDeletionEntity(tableName = "playlist_history", rowKey = id.toString()))
            refresh()
            true
        } catch (e: Exception) {
            Log.e(TAG, "delete playlist failed", e)
            false
        }
    }

    /** Desktop get_playlist_songs(): links whose file still exists, with tags. */
    suspend fun songsIn(playlistId: Long, musicDir: File): List<SongMeta> = withContext(Dispatchers.IO) {
        db.playlistDao().getLinks(playlistId).mapNotNull { link ->
            val f = File(musicDir, link.songFile)
            // Cache-first: avoids a MediaMetadataRetriever open per row.
            if (f.exists()) metadata.metaFor(musicDir, link.songFile).copy(dateAdded = link.dateAdded) else null
        }
    }

    suspend fun removeSong(playlistId: Long, file: String) = withContext(Dispatchers.IO) {
        // Targeted delete (desktop replaces the whole set; single-remove is equivalent).
        val pl = db.playlistDao()
        val keep = pl.getIdsForSong(file).filter { it != playlistId }
        pl.deleteLinksForSong(file)
        keep.forEach { pid ->
            pl.insertLink(SongPlaylistEntity(songFile = file, playlistId = pid, dateAdded = nowStamp()))
        }
    }

    suspend fun idsForSong(file: String): List<Long> = withContext(Dispatchers.IO) {
        db.playlistDao().getIdsForSong(file)
    }

    /** Desktop update_song_playlists(): ensure song row, replace association set. */
    suspend fun setSongPlaylists(file: String, title: String, playlistIds: List<Long>): Boolean =
        withContext(Dispatchers.IO) {
            try {
                db.songDao().insertIgnore(SongEntity(file = file, downloadedLink = null, title = title, dateDownload = null, artist = null))
                val pl = db.playlistDao()
                pl.deleteLinksForSong(file)
                playlistIds.forEach { pid ->
                    pl.insertLink(SongPlaylistEntity(songFile = file, playlistId = pid, dateAdded = nowStamp()))
                }
                refresh()
                true
            } catch (e: Exception) {
                Log.e(TAG, "setSongPlaylists failed", e)
                false
            }
        }

    private fun nowStamp(): String = STAMP.format(Date())

    companion object {
        private const val TAG = "PlaylistRepository"
        private val STAMP = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }
    }
}
