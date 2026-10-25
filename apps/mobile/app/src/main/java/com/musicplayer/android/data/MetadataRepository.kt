package com.musicplayer.android.data

import android.content.Context
import android.media.MediaMetadataRetriever
import android.util.Log
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.SyncDeletionEntity
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.jaudiotagger.audio.AudioFileIO
import org.jaudiotagger.tag.FieldKey
import org.jaudiotagger.tag.images.ArtworkFactory
import java.io.File

/**
 * Port of desktop `MetadataManager` (services/metadata.py, mutagen-based).
 *
 * Read path: framework MediaMetadataRetriever (no dep, same fields: title /
 * artist / album / year / duration + embedded picture), with the same defaults
 * desktop uses ('Unknown', filename minus .mp3, duration 0, no cover).
 *
 * Write path: jaudiotagger-android (Android fork — stock needs API 26+ nio),
 * TIT2/TPE1/TALB/TDRC + APIC equivalents, v2.3-style save via commit().
 * Room Songs title/artist updated too, like desktop's DB update.
 *
 * Delete path: stop-n/a (no player yet), remove file, delete the same four
 * local rows, record the same three tombstones.
 */
class MetadataRepository(context: Context, val engine: DownloadEngine) {

    private val appContext = context.applicationContext
    private val db = AppDatabase.get(appContext)

    private val _library = MutableStateFlow<List<SongMeta>>(emptyList())
    val library: StateFlow<List<SongMeta>> = _library.asStateFlow()

    private val _status = MutableStateFlow<String?>(null)
    val status: StateFlow<String?> = _status.asStateFlow()

    // Small in-memory cover caches, both bounded by BYTES (the old
    // count-capped map could hold 64 full-size images; thumbs decode sampled).
    private val coverCache = object : android.util.LruCache<String, ByteArray>(8 * 1024) {
        override fun sizeOf(key: String, value: ByteArray): Int = value.size / 1024
    }
    private val artCache = object : android.util.LruCache<String, android.graphics.Bitmap>(24 * 1024) {
        override fun sizeOf(key: String, value: android.graphics.Bitmap): Int = value.byteCount / 1024
    }

    // Tag cache: refreshLibrary() used to re-open EVERY mp3 with
    // MediaMetadataRetriever on each call (launch, pull-refresh, every edit,
    // every download batch) — the main battery/CPU drain on big libraries.
    // Entries are keyed by ABSOLUTE PATH (songs and podcasts are separate
    // libraries; same-named files must never collide) and validated by
    // lastModified+length, so a refresh only pays for new/changed files.
    private data class CacheEntry(val lastModified: Long, val length: Long, val meta: SongMeta)
    private val metaCache = mutableMapOf<String, CacheEntry>()
    private val refreshMutex = Mutex()

    /**
     * Mirror of send_song_list(): mp3s on disk + tags + Room date_download.
     * Incremental: unchanged files are served from [metaCache], so repeated
     * refreshes cost a directory listing, not N retriever opens.
     */
    suspend fun refreshLibrary() = withContext(Dispatchers.IO) {
        refreshMutex.withLock {
            val outDir = engine.outputDir()
            val files = outDir.listFiles { f ->
                f.isFile && f.extension.equals("mp3", ignoreCase = true)
            }?.sortedBy { it.name } ?: emptyList()
            val dates = db.songDao().getAll().associate { it.file to it.dateDownload }
            val fresh = files.map { f ->
                val lm = f.lastModified()
                val len = f.length()
                val cached = synchronized(metaCache) { metaCache[f.absolutePath] }
                if (cached != null && cached.lastModified == lm && cached.length == len) {
                    cached.meta.copy(dateDownload = dates[f.name])
                } else {
                    val m = readMeta(f).copy(dateDownload = dates[f.name])
                    // Cache without the DB date (it can change w/o file change).
                    synchronized(metaCache) { metaCache[f.absolutePath] = CacheEntry(lm, len, m.copy(dateDownload = null)) }
                    m
                }
            }
            val liveKeys = fresh.map { File(outDir, it.file).absolutePath }.toSet()
            synchronized(metaCache) {
                // Only evict THIS library's stale keys (never another dir's).
                metaCache.keys.retainAll { k -> !k.startsWith(outDir.absolutePath) || k in liveKeys }
            }
            _library.value = fresh
        }
    }

    /**
     * Single-file metadata, cache-first. History/playlist screens used to call
     * readMeta() per row (another full retriever sweep per screen open);
     * this shares the library cache instead.
     */
    suspend fun metaFor(musicDir: File, fileName: String): SongMeta = withContext(Dispatchers.IO) {
        val f = File(musicDir, fileName)
        if (f.exists()) {
            val lm = f.lastModified()
            val len = f.length()
            synchronized(metaCache) { metaCache[f.absolutePath] }
                ?.takeIf { it.lastModified == lm && it.length == len }
                ?.let { return@withContext it.meta }
            val m = readMeta(f)
            synchronized(metaCache) { metaCache[f.absolutePath] = CacheEntry(lm, len, m) }
            m
        } else {
            SongMeta(fileName, fileName, "Unknown", "Unknown", "Unknown", 0L, null, false)
        }
    }

    /** Drop cached entries for a file name in BOTH libraries. */
    fun invalidateCache(fileName: String) {
        synchronized(metaCache) {
            metaCache.remove(File(engine.outputDir(), fileName).absolutePath)
            try {
                metaCache.remove(File(engine.podcastsDir(), fileName).absolutePath)
            } catch (_: Exception) {
            }
        }
    }

    /**
     * Display title for a file name: strip a leading YouTube-id prefix
     * (id_title.mp3 naming stays on disk for desktop parity; only display
     * is cleaned) and undo restrictfilenames' underscore joins.
     */
    fun fallbackTitle(fileName: String): String {
        val base = File(fileName).nameWithoutExtension
        return base
            .replace(Regex("^[A-Za-z0-9_-]{11}_"), "")
            .replace('_', ' ')
            .replace(Regex("\\s+"), " ")
            .trim()
            .ifBlank { base }
    }

    /** File overload of [metaFor], for callers that already hold a File. */
    suspend fun metaFor(file: File): SongMeta =
        metaFor(file.parentFile ?: engine.outputDir(), file.name)

    fun readMeta(file: File): SongMeta {
        var title = fallbackTitle(file.name)
        var artist = "Unknown"
        var album = "Unknown"
        var year = "Unknown"
        var durationSec = 0L
        var hasCover = false
        try {
            MediaMetadataRetriever().use { r ->
                r.setDataSource(file.absolutePath)
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)?.takeIf { it.isNotBlank() }?.let { title = it }
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)?.takeIf { it.isNotBlank() }?.let { artist = it }
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ALBUM)?.takeIf { it.isNotBlank() }?.let { album = it }
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_YEAR)?.takeIf { it.isNotBlank() }?.let { year = it }
                r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull()?.let { durationSec = it / 1000 }
                hasCover = r.embeddedPicture != null
            }
        } catch (e: Exception) {
            Log.w(TAG, "Failed to load metadata for ${file.name}: ${e.message}")
        }
        return SongMeta(file.name, title, artist, album, year, durationSec, dateDownload = null, hasCover)
    }

    /** Embedded picture bytes for a row (cached); null when absent/unreadable. */
    suspend fun coverBytes(fileName: String): ByteArray? =
        coverBytesFor(File(engine.outputDir(), fileName))

    /**
     * File-based cover bytes (every queue item carries its library flag and
     * resolves to the correct folder: songs vs podcasts). Cache is keyed by
     * absolute path so same-named files in both libraries never collide.
     */
    suspend fun coverBytesFor(file: File): ByteArray? = withContext(Dispatchers.IO) {
        val key = file.absolutePath
        synchronized(coverCache) { coverCache.get(key) }?.let { return@withContext it }
        try {
            MediaMetadataRetriever().use { r ->
                r.setDataSource(file.absolutePath)
                val pic = r.embeddedPicture
                if (pic != null) {
                    synchronized(coverCache) { coverCache.put(key, pic) }
                }
                return@withContext pic
            }
        } catch (e: Exception) {
            Log.w(TAG, "No cover for ${file.name}: ${e.message}")
            return@withContext null
        }
    }

    /**
     * Display-ready cover, downsampled to maxWidthPx (bounds-first decode)
     * and cached by file+size. All list/notification/cover UI must use this
     * instead of decoding raw bytes at full resolution.
     */
    suspend fun coverArt(fileName: String, maxWidthPx: Int): android.graphics.Bitmap? =
        coverArtFor(File(engine.outputDir(), fileName), maxWidthPx)

    /**
     * File-based cover (songs folder vs podcasts folder resolved by the
     * caller from the item's library flag).
     */
    suspend fun coverArtFor(file: File, maxWidthPx: Int): android.graphics.Bitmap? =
        withContext(Dispatchers.IO) {
            val key = "${file.absolutePath}@$maxWidthPx"
            synchronized(artCache) { artCache.get(key) }?.let { return@withContext it }
            val bytes = coverBytesFor(file) ?: return@withContext null
            // A flaky decode must never break playback or saves: log-and-continue.
            val bmp = try {
                com.musicplayer.android.ui.ImageLoader.decodeSampled(bytes, maxWidthPx)
            } catch (e: Exception) {
                Log.w(TAG, "cover decode failed for ${file.name}: ${e.message}")
                null
            } ?: return@withContext null
            synchronized(artCache) { artCache.put(key, bmp) }
            bmp
        }

    /** Cover resolved to the correct folder from the song's library flag. */
    suspend fun coverForSong(song: SongMeta, maxWidthPx: Int): android.graphics.Bitmap? {
        val dir = if (song.isPodcast) engine.podcastsDir() else engine.outputDir()
        return coverArtFor(File(dir, song.file), maxWidthPx)
    }

    /**
     * Mirror of update_song_metadata(): write tags, optionally drop the cover,
     * update Room title/artist. Returns true on success, false with status set.
     * (Desktop also reloads the player when editing the current song — no
     * player exists yet on Android; that wires in with the playback phase.)
     */
    /**
     * Mirror of update_song_metadata(): write tags, optionally drop the cover,
     * update the owning table (Songs or Podcasts). Edit Info writes tags to
     * the podcast folder + Podcasts table when [isPodcast] is set.
     */
    suspend fun updateMeta(
        fileName: String,
        title: String,
        artist: String,
        album: String,
        year: String,
        removeCover: Boolean,
        genre: String? = null,
        isPodcast: Boolean = false
    ): Boolean = withContext(Dispatchers.IO) {
        try {
            val dir = if (isPodcast) engine.podcastsDir() else engine.outputDir()
            val target = File(dir, fileName)
            if (!target.exists()) {
                _status.value = "File not found: $fileName"
                return@withContext false
            }
            val audio = AudioFileIO.read(target)
            val tag = audio.tagOrCreateAndSetDefault
            tag.setField(FieldKey.TITLE, title)
            tag.setField(FieldKey.ARTIST, artist)
            tag.setField(FieldKey.ALBUM, album)
            tag.setField(FieldKey.YEAR, year)
            if (!genre.isNullOrBlank()) {
                tag.setField(FieldKey.GENRE, genre)
            }
            if (removeCover) {
                tag.deleteArtworkField()
            }
            audio.commit()
            if (removeCover) {
                synchronized(coverCache) { coverCache.remove(target.absolutePath) }
            }
            if (isPodcast) db.podcastDao().updateTitleArtist(fileName, title, artist)
            else db.songDao().updateTitleArtist(fileName, title, artist)
            _status.value = "Saved: $title"
            invalidateCache(fileName)
            if (!isPodcast) refreshLibrary()
            true
        } catch (e: Exception) {
            Log.e(TAG, "Error updating metadata for $fileName", e)
            _status.value = "Save failed: ${e.message}"
            false
        }
    }

    /** Replace the embedded cover with raw image bytes (jpeg/png). */
    suspend fun setCoverBytes(
        fileName: String,
        image: ByteArray,
        mime: String,
        isPodcast: Boolean = false
    ): Boolean =
        withContext(Dispatchers.IO) {
            try {
                val dir = if (isPodcast) engine.podcastsDir() else engine.outputDir()
                val target = File(dir, fileName)
                if (!target.exists()) {
                    _status.value = "File not found: $fileName"
                    return@withContext false
                }
                val audio = AudioFileIO.read(target)
                val tag = audio.tagOrCreateAndSetDefault
                tag.deleteArtworkField()
                // ArtworkFactory only takes files, so stage the bytes (cacheDir, deleted after).
                val tmp = File.createTempFile("cover", ".img", appContext.cacheDir)
                try {
                    tmp.writeBytes(image)
                    tag.setField(ArtworkFactory.createArtworkFromFile(tmp))
                } finally {
                    tmp.delete()
                }
                audio.commit()
                synchronized(coverCache) { coverCache.put(target.absolutePath, image) }
                _status.value = "Cover updated"
                invalidateCache(fileName)
                if (!isPodcast) refreshLibrary()
                true
            } catch (e: Exception) {
                Log.e(TAG, "Error setting cover for $fileName", e)
                _status.value = "Cover update failed: ${e.message}"
                false
            }
        }

    /**
     * Single-commit variant of updateMeta()+setCoverBytes() for the download
     * auto-tagger: tags + cover land in ONE jaudiotagger read/commit cycle
     * (each commit rewrites the whole file) and ONE library refresh, instead
     * of two full MP3 rewrites back to back.
     */
    suspend fun updateMetaWithCover(
        fileName: String,
        title: String,
        artist: String,
        album: String,
        year: String,
        genre: String?,
        cover: ByteArray?
    ): Boolean = withContext(Dispatchers.IO) {
        try {
            val target = File(engine.outputDir(), fileName)
            if (!target.exists()) {
                _status.value = "File not found: $fileName"
                return@withContext false
            }
            val audio = AudioFileIO.read(target)
            val tag = audio.tagOrCreateAndSetDefault
            tag.setField(FieldKey.TITLE, title)
            tag.setField(FieldKey.ARTIST, artist)
            tag.setField(FieldKey.ALBUM, album)
            tag.setField(FieldKey.YEAR, year)
            if (!genre.isNullOrBlank()) {
                tag.setField(FieldKey.GENRE, genre)
            }
            if (cover != null) {
                tag.deleteArtworkField()
                // ArtworkFactory only takes files, so stage the bytes (cacheDir, deleted after).
                val tmp = File.createTempFile("cover", ".img", appContext.cacheDir)
                try {
                    tmp.writeBytes(cover)
                    tag.setField(ArtworkFactory.createArtworkFromFile(tmp))
                } finally {
                    tmp.delete()
                }
            }
            audio.commit()
            synchronized(coverCache) {
                if (cover != null) coverCache.put(target.absolutePath, cover)
            }
            db.songDao().updateTitleArtist(fileName, title, artist)
            _status.value = "Saved: $title"
            invalidateCache(fileName)
            refreshLibrary()
            true
        } catch (e: Exception) {
            Log.e(TAG, "Error updating metadata for $fileName", e)
            _status.value = "Save failed: ${e.message}"
            false
        }
    }

    /** Mirror of delete_song(): remove file + the same four local rows + tombstones. */
    suspend fun deleteSong(fileName: String): Boolean = withContext(Dispatchers.IO) {
        try {
            val target = File(engine.outputDir(), fileName)
            if (target.exists() && !target.delete()) {
                _status.value = "Could not delete file: $fileName"
                return@withContext false
            }
            val dao = db.songDao()
            dao.deleteLinks(fileName)
            dao.deleteLyrics(fileName)
            dao.deleteHistory(fileName)
            dao.deleteSong(fileName)
            dao.recordDeletion(SyncDeletionEntity(tableName = "songs", rowKey = fileName))
            dao.recordDeletion(SyncDeletionEntity(tableName = "lyrics", rowKey = fileName))
            dao.recordDeletion(SyncDeletionEntity(tableName = "music_history", rowKey = fileName))
            synchronized(coverCache) { coverCache.remove(target.absolutePath) }
            _status.value = "Deleted: $fileName"
            invalidateCache(fileName)
            refreshLibrary()
            true
        } catch (e: Exception) {
            Log.e(TAG, "Error deleting $fileName", e)
            _status.value = "Delete failed: ${e.message}"
            false
        }
    }

    fun clearStatus() {
        _status.value = null
    }

    companion object {
        private const val TAG = "MetadataRepository"
    }
}
