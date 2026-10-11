package com.musicplayer.android.data

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.db.PodcastChapterEntity
import com.musicplayer.android.data.db.PodcastEntity
import com.musicplayer.android.data.db.PodcastTagEntity
import com.musicplayer.android.data.db.PodcastTagLink
import com.musicplayer.android.data.db.SyncDeletion
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File

// Podcast library: episodes + tags + chapter marks (desktop Podcasts table,
// Podcast_Tags/Tag_Links + Podcast_Chapters, 1:1). Listing is lightweight
// (DB rows only); full metadata loads on demand via MetadataRepository.
data class PodcastChapter(
    val id: Long,
    val file: String,
    val name: String,
    val startSecs: Double,
    val endSecs: Double?,
)

class PodcastRepository(
    context: Context,
    private val engine: DownloadEngine,
    private val metadata: MetadataRepository,
) {
    data class PodcastTag(val id: Long, val name: String, val episodeCount: Int)

    private val appCtx = context.applicationContext
    private val db by lazy { AppDatabase.get(appCtx) }

    private val _episodes = MutableStateFlow<List<SongMeta>>(emptyList())
    val episodes: StateFlow<List<SongMeta>> = _episodes.asStateFlow()

    private val _tags = MutableStateFlow<List<PodcastTag>>(emptyList())
    val tags: StateFlow<List<PodcastTag>> = _tags.asStateFlow()

    private val _tagsByFile = MutableStateFlow<Map<String, List<Long>>>(emptyMap())
    val tagsByFile: StateFlow<Map<String, List<Long>>> = _tagsByFile.asStateFlow()

    private fun podsDir(): File = try {
        engine.podcastsDir()
    } catch (_: Exception) {
        File(appCtx.filesDir, "podcasts")
    }

    private fun toMeta(row: PodcastEntity): SongMeta = SongMeta(
        file = row.file,
        title = row.title,
        artist = row.artist.orEmpty(),
        album = "",
        durationSec = 0,
        dateDownload = row.dateDownloadMillis.takeIf { it > 0 }?.let {
            java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.US)
                .format(java.util.Date(it))
        },
        isPodcast = true,
    )

    suspend fun refresh() {
        try {
            val rows = db.podcastDao().all()
            _episodes.value = rows.map(::toMeta)
            val links = db.podcastTagLinkDao().all()
            _tagsByFile.value = links.groupBy({ it.podcastFile }, { it.tagId })
            val counts = links.groupingBy { it.tagId }.eachCount()
            _tags.value = db.podcastTagDao().all().map {
                PodcastTag(it.id, it.name, counts[it.id] ?: 0)
            }
        } catch (e: Exception) {
            Log.w(TAG, "podcast refresh failed", e)
        }
    }

    /** Scan the podcasts folder into Podcasts (desktop sync_local_podcasts_to_db). */
    suspend fun rescan(): String {
        return try {
            val dir = podsDir()
            if (!dir.exists()) return "Folder does not exist."
            val files = dir.listFiles { f -> f.isFile && f.extension.equals("mp3", ignoreCase = true) }
                ?: return "Folder does not exist."
            var added = 0
            var updated = 0
            for (f in files) {
                val row = db.podcastDao().byFile(f.name)
                if (row == null) {
                    db.podcastDao().insertIgnore(
                        PodcastEntity(f.name, null, f.nameWithoutExtension, f.lastModified(), null),
                    )
                    added++
                }
            }
            refresh()
            "Sync complete: $added added, $updated updated."
        } catch (e: Exception) {
            Log.w(TAG, "podcast rescan failed", e)
            "Error: ${e.message}"
        }
    }

    /** Full metadata for one episode (cover excluded here; callers use ArtworkRepository). */
    suspend fun refreshFromTags(file: String): SongMeta? {
        return try {
            metadata.metaFor(File(podsDir(), file))
        } catch (_: Exception) {
            try {
                db.podcastDao().byFile(file)?.let(::toMeta)
            } catch (_: Exception) {
                null
            }
        }
    }

    suspend fun updateInfo(file: String, title: String, artist: String) {
        try {
            val row = db.podcastDao().byFile(file) ?: return
            db.podcastDao().update(row.copy(title = title, artist = artist.takeIf { it.isNotBlank() }))
            refresh()
        } catch (e: Exception) {
            Log.w(TAG, "episode update failed", e)
        }
    }

    /** Delete file + rows + tombstones (desktop delete_podcast, 1:1). */
    suspend fun delete(file: String) {
        try {
            File(podsDir(), file).takeIf { it.exists() }?.delete()
        } catch (_: Exception) {
        }
        try {
            db.podcastTagLinkDao().deleteForEpisode(file)
            db.podcastDao().deleteByFile(file)
            db.podcastChapterDao().deleteForEpisode(file)
            db.syncDeletionDao().insert(SyncDeletion(tableName = "podcasts", rowKey = file))
            db.syncDeletionDao().insert(SyncDeletion(tableName = "podcast_chapters", rowKey = file))
            refresh()
        } catch (e: Exception) {
            Log.w(TAG, "episode delete failed", e)
        }
    }

    suspend fun createTag(name: String): Long {
        val clean = name.trim()
        if (clean.isEmpty()) return -1L
        return try {
            val existing = db.podcastTagDao().all().firstOrNull { it.name == clean }
            if (existing != null) {
                existing.id
            } else {
                val id = db.podcastTagDao().insertIgnore(PodcastTagEntity(name = clean))
                refresh()
                if (id == -1L) {
                    db.podcastTagDao().all().firstOrNull { it.name == clean }?.id ?: -1L
                } else {
                    id
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "tag create failed", e)
            -1L
        }
    }

    suspend fun renameTag(id: Long, name: String): Boolean {
        val clean = name.trim()
        if (clean.isEmpty()) return false
        return try {
            val clash = db.podcastTagDao().all().firstOrNull { it.name == clean }
            if (clash != null && clash.id != id) return false
            val row = db.podcastTagDao().byId(id) ?: return false
            db.podcastTagDao().update(row.copy(name = clean))
            refresh()
            true
        } catch (e: Exception) {
            Log.w(TAG, "tag rename failed", e)
            false
        }
    }

    suspend fun deleteTag(id: Long) {
        try {
            db.podcastTagLinkDao().deleteForTags(listOf(id))
            db.podcastTagDao().deleteById(id)
            db.syncDeletionDao().insert(SyncDeletion(tableName = "podcast_tags", rowKey = id.toString()))
            refresh()
        } catch (e: Exception) {
            Log.w(TAG, "tag delete failed", e)
        }
    }

    suspend fun assignTag(file: String, tagId: Long): Boolean {
        return try {
            db.podcastTagLinkDao().insertIgnore(PodcastTagLink(podcastFile = file, tagId = tagId))
            refresh()
            true
        } catch (e: Exception) {
            Log.w(TAG, "tag assign failed", e)
            false
        }
    }

    suspend fun unassignTag(file: String, tagId: Long): Boolean {
        return try {
            db.podcastTagLinkDao().deleteLink(file, tagId)
            refresh()
            true
        } catch (e: Exception) {
            Log.w(TAG, "tag unassign failed", e)
            false
        }
    }

    // ---- Chapter marks (desktop Podcast_Chapters + web PodcastTimestamp) ----

    private fun validateChapter(name: String, startSecs: Double, endSecs: Double?): String? {
        val clean = name.trim()
        if (clean.isEmpty()) return "Timestamp name can't be blank."
        if (clean.length > 255) return "Timestamp name is too long (max 255)."
        if (startSecs.isNaN() || startSecs.isInfinite() || startSecs < 0) {
            return "Start time must be zero or later."
        }
        if (endSecs != null && (endSecs.isNaN() || endSecs.isInfinite() || endSecs <= startSecs)) {
            return "End time must be later than the start time."
        }
        return null
    }

    suspend fun chaptersFor(file: String): List<PodcastChapter> {
        return try {
            db.podcastChapterDao().byEpisode(file).map {
                PodcastChapter(it.id, it.podcastFile, it.name, it.startSecs, it.endSecs)
            }
        } catch (e: Exception) {
            Log.w(TAG, "chapters load failed", e)
            emptyList()
        }
    }

    suspend fun createChapter(file: String, name: String, startSecs: Double, endSecs: Double?): Long {
        if (!File(podsDir(), file).exists()) return -1L
        if (validateChapter(name, startSecs, endSecs) != null) return -1L
        return try {
            val id = db.podcastChapterDao().insertIgnore(
                PodcastChapterEntity(
                    podcastFile = file, name = name.trim(),
                    startSecs = startSecs, endSecs = endSecs,
                ),
            )
            if (id == -1L) -1L else id
        } catch (e: Exception) {
            Log.w(TAG, "chapter create failed", e)
            -1L
        }
    }

    suspend fun updateChapter(id: Long, name: String, startSecs: Double, endSecs: Double?): Boolean {
        if (validateChapter(name, startSecs, endSecs) != null) return false
        return try {
            val row = db.podcastChapterDao().byId(id) ?: return false
            db.podcastChapterDao().update(
                row.copy(name = name.trim(), startSecs = startSecs, endSecs = endSecs),
            )
            true
        } catch (e: Exception) {
            Log.w(TAG, "chapter update failed", e)
            false
        }
    }

    suspend fun deleteChapter(id: Long): Boolean {
        return try {
            db.podcastChapterDao().deleteById(id) > 0
        } catch (e: Exception) {
            Log.w(TAG, "chapter delete failed", e)
            false
        }
    }

    companion object {
        private const val TAG = "PodcastRepository"
    }
}
