package com.musicplayer.android.data

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.Log
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.db.SongEntity
import com.musicplayer.android.engine.DownloadEngine
import com.musicplayer.android.playback.PlaybackSource
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject

// App prefs + queue persistence (desktop settings.py + Settings row +
// playback _persist_queue/_persist_source, 1:1). Backed by the "hathor"
// SharedPreferences (same store screens read directly) with StateFlow
// mirrors for Compose. Volume/queue/source writes apply live; persist*()
// flush the rest. Chapter auto-skip follows the crossfade pattern exactly.
class SettingsRepository(
    context: Context,
    private val engine: DownloadEngine,
    private val metadata: MetadataRepository,
) {
    data class QueuedFile(val file: String, val isPodcast: Boolean)
    data class SavedQueue(val files: List<QueuedFile>, val index: Int)

    var onCrossfadeChanged: ((Boolean, Float) -> Unit)? = null
    var onLimitChanged: ((Int) -> Unit)? = null
    var onChapterSkipChanged: ((Boolean) -> Unit)? = null

    private val appCtx = context.applicationContext
    private val prefs = appCtx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val db by lazy { AppDatabase.get(appCtx) }

    private val _volume = MutableStateFlow(prefs.getFloat(KEY_VOLUME, 0.7f))
    val volume: StateFlow<Float> = _volume.asStateFlow()

    private val _limit = MutableStateFlow(prefs.getInt(KEY_LIMIT, 3).coerceIn(1, 20))
    val limit: StateFlow<Int> = _limit.asStateFlow()

    private val _background = MutableStateFlow<Bitmap?>(null)
    val background: StateFlow<Bitmap?> = _background.asStateFlow()

    private val _crossfadeEnabled = MutableStateFlow(prefs.getBoolean(KEY_XFADE_ON, false))
    val crossfadeEnabled: StateFlow<Boolean> = _crossfadeEnabled.asStateFlow()

    private val _crossfadeSeconds = MutableStateFlow(prefs.getFloat(KEY_XFADE_SECS, 5f))
    val crossfadeSeconds: StateFlow<Float> = _crossfadeSeconds.asStateFlow()

    private val _chapterSkip = MutableStateFlow(prefs.getBoolean(KEY_CHAPTER_SKIP, false))
    val chapterSkip: StateFlow<Boolean> = _chapterSkip.asStateFlow()

    /** Reload every flow from prefs (startup + Settings entry). */
    suspend fun load() {
        _volume.value = prefs.getFloat(KEY_VOLUME, 0.7f)
        _limit.value = prefs.getInt(KEY_LIMIT, 3).coerceIn(1, 20)
        _crossfadeEnabled.value = prefs.getBoolean(KEY_XFADE_ON, false)
        _crossfadeSeconds.value = prefs.getFloat(KEY_XFADE_SECS, 5f)
        _chapterSkip.value = prefs.getBoolean(KEY_CHAPTER_SKIP, false)
        _background.value = decodeBackground(prefs.getString(KEY_BACKGROUND, null))
    }

    fun musicFolder(): String = prefs.getString(KEY_MUSIC_FOLDER, null)
        ?: try { engine.outputDir().absolutePath } catch (_: Exception) { "" }

    fun podcastsFolder(): String = prefs.getString(KEY_PODCASTS_FOLDER, null)
        ?: try { engine.podcastsDir().absolutePath } catch (_: Exception) { "" }

    suspend fun changeFolder(uri: Uri): String {
        return try {
            appCtx.contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
            prefs.edit().putString(KEY_MUSIC_FOLDER, uri.toString()).apply()
            "Music folder updated."
        } catch (e: Exception) {
            Log.w(TAG, "folder change failed", e)
            "Could not use that folder."
        }
    }

    suspend fun changePodcastsFolder(uri: Uri): String {
        return try {
            appCtx.contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
            prefs.edit().putString(KEY_PODCASTS_FOLDER, uri.toString()).apply()
            "Podcasts folder updated."
        } catch (e: Exception) {
            Log.w(TAG, "podcasts folder change failed", e)
            "Could not use that folder."
        }
    }

    suspend fun setBackground(uri: Uri): String {
        return try {
            appCtx.contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION,
            )
            prefs.edit().putString(KEY_BACKGROUND, uri.toString()).apply()
            _background.value = decodeBackground(uri.toString())
            "Wallpaper updated."
        } catch (e: Exception) {
            Log.w(TAG, "wallpaper set failed", e)
            "Could not load that image."
        }
    }

    suspend fun removeBackground(): String {
        prefs.edit().remove(KEY_BACKGROUND).apply()
        _background.value = null
        return "Wallpaper removed."
    }

    /** Scan the music dir into Songs (desktop sync_local_songs_to_db). */
    suspend fun rescan(): String {
        return try {
            val dir = engine.outputDir()
            val files = dir.listFiles { f -> f.isFile && f.extension.equals("mp3", ignoreCase = true) }
                ?: return "Folder does not exist."
            var added = 0
            var updated = 0
            for (f in files) {
                val row = db.songDao().byFile(f.name)
                if (row == null) {
                    db.songDao().insertIgnore(
                        SongEntity(f.name, null, f.nameWithoutExtension, f.lastModified(), null),
                    )
                    added++
                }
            }
            try {
                metadata.refreshLibrary()
            } catch (_: Exception) {
            }
            "Sync complete: $added added, $updated updated."
        } catch (e: Exception) {
            Log.w(TAG, "rescan failed", e)
            "Error: ${e.message}"
        }
    }

    fun remoteStatus(): String {
        return try { SyncConfig.describe() } catch (_: Exception) { "Sync off." }
    }

    fun setVolume(v: Float) {
        _volume.value = v.coerceIn(0f, 1f)
    }

    suspend fun persistVolume() {
        prefs.edit().putFloat(KEY_VOLUME, _volume.value).apply()
    }

    suspend fun setCrossfade(enabled: Boolean, seconds: Float) {
        val secs = seconds.coerceIn(0f, 12f)
        _crossfadeEnabled.value = enabled
        _crossfadeSeconds.value = secs
        prefs.edit().putBoolean(KEY_XFADE_ON, enabled).putFloat(KEY_XFADE_SECS, secs).apply()
        try {
            onCrossfadeChanged?.invoke(enabled, secs)
        } catch (_: Exception) {
        }
    }

    suspend fun setLimit(n: Int) {
        val v = n.coerceIn(1, 20)
        _limit.value = v
        prefs.edit().putInt(KEY_LIMIT, v).apply()
        try {
            onLimitChanged?.invoke(v)
        } catch (_: Exception) {
        }
    }

    suspend fun setChapterSkip(enabled: Boolean) {
        _chapterSkip.value = enabled
        prefs.edit().putBoolean(KEY_CHAPTER_SKIP, enabled).apply()
        try {
            onChapterSkipChanged?.invoke(enabled)
        } catch (_: Exception) {
        }
    }

    suspend fun saveQueueFiles(files: List<QueuedFile>) {
        val arr = JSONArray()
        for (f in files) {
            arr.put(JSONObject().put("file", f.file).put("isPodcast", f.isPodcast))
        }
        prefs.edit().putString(KEY_QUEUE, arr.toString()).apply()
    }

    suspend fun loadQueue(): SavedQueue? {
        val raw = prefs.getString(KEY_QUEUE, null) ?: return null
        return try {
            val arr = JSONArray(raw)
            val files = buildList {
                for (i in 0 until arr.length()) {
                    val o = arr.getJSONObject(i)
                    add(QueuedFile(o.getString("file"), o.optBoolean("isPodcast", false)))
                }
            }
            if (files.isEmpty()) return null
            val current = loadCurrentSongName()
            val index = files.indexOfFirst { it.file == current }.coerceAtLeast(0)
            SavedQueue(files, index)
        } catch (_: Exception) {
            null
        }
    }

    suspend fun saveCurrentSong(name: String) {
        prefs.edit().putString(KEY_CURRENT_SONG, name).apply()
    }

    suspend fun loadCurrentSongName(): String? = prefs.getString(KEY_CURRENT_SONG, null)

    suspend fun saveQueueSource(source: PlaybackSource?) {
        prefs.edit()
            .putString(KEY_QUEUE_SRC_TYPE, source?.type)
            .putString(KEY_QUEUE_SRC_ID, source?.id)
            .apply()
    }

    suspend fun loadQueueSource(): PlaybackSource? {
        val type = prefs.getString(KEY_QUEUE_SRC_TYPE, null) ?: return null
        return PlaybackSource(type, prefs.getString(KEY_QUEUE_SRC_ID, null))
    }

    private fun decodeBackground(uriString: String?): Bitmap? {
        if (uriString.isNullOrBlank()) return null
        return try {
            val uri = Uri.parse(uriString)
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            appCtx.contentResolver.openInputStream(uri)?.use {
                BitmapFactory.decodeStream(it, null, bounds)
            }
            var sample = 1
            val maxPx = 1920
            while (bounds.outWidth / sample > maxPx || bounds.outHeight / sample > maxPx) sample *= 2
            val opts = BitmapFactory.Options().apply { inSampleSize = sample }
            appCtx.contentResolver.openInputStream(uri)?.use {
                BitmapFactory.decodeStream(it, null, opts)
            }
        } catch (e: Exception) {
            Log.w(TAG, "wallpaper decode failed", e)
            null
        }
    }

    companion object {
        private const val TAG = "SettingsRepository"
        private const val PREFS = "hathor"
        private const val KEY_VOLUME = "volume"
        private const val KEY_LIMIT = "limit_downloads"
        private const val KEY_XFADE_ON = "crossfade_enabled"
        private const val KEY_XFADE_SECS = "crossfade_seconds"
        private const val KEY_CHAPTER_SKIP = "chapter_skip"
        private const val KEY_MUSIC_FOLDER = "music_folder"
        private const val KEY_PODCASTS_FOLDER = "podcasts_folder"
        private const val KEY_BACKGROUND = "background"
        private const val KEY_QUEUE = "queue_files"
        private const val KEY_CURRENT_SONG = "current_song"
        private const val KEY_QUEUE_SRC_TYPE = "queue_source_type"
        private const val KEY_QUEUE_SRC_ID = "queue_source_id"
    }
}
