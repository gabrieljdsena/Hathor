package com.musicplayer.android.data

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.DownloadJobEntity
import com.musicplayer.android.data.local.SongEntity
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.sync.withPermit
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID

/**
 * Port of desktop DownloadManager (services/downloads.py): persisted job log
 * (Download_Queue table), bounded concurrency (limit 1..20, default 3),
 * progress per job, retry, song-record upsert on success.
 *
 * Differences, stated: scheduling is coroutine-based (no polling thread);
 * cancel applies to queued jobs (a running yt-dlp run has no cancel handle,
 * so the button only shows for queued); jobs stuck active from a killed
 * process are re-queued on launch; completion of a drained queue fires
 * onIdle (desktop's idle callback refreshed the song list).
 */
data class JobItem(
    val qid: String,
    val url: String?,
    val title: String,
    val artist: String?,
    val status: String,
    val progress01: Float,
    val error: String?,
    val filename: String?,
    val isPodcast: Boolean = false
) {
    val isTerminal: Boolean get() = status == "done" || status == "failed" || status == "cancelled"
}

class QueueRepository(
    context: Context,
    private val engine: DownloadEngine,
    var onIdle: (() -> Unit)? = null,
    private val startGate: suspend () -> Unit = {},
    private val metadata: MetadataRepository? = null,
    private val artwork: ArtworkRepository? = null
) {
    private val appContext = context.applicationContext
    private val db = AppDatabase.get(appContext)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val pumpMutex = Mutex()
    private var semaphore = Semaphore(3)

    private val _jobs = MutableStateFlow<List<JobItem>>(emptyList())
    val jobs: StateFlow<List<JobItem>> = _jobs.asStateFlow()

    private val _total = MutableStateFlow(0)
    val total: StateFlow<Int> = _total.asStateFlow()

    private val _activeCount = MutableStateFlow(0)
    val activeCount: StateFlow<Int> = _activeCount.asStateFlow()

    // Jobs the user cancelled while running (kill confirmed via watchdog/catch).
    private val userCancelled = mutableSetOf<String>()

    init {
        scope.launch {
            // Wait for the yt-dlp self-update so a binary swap can't collide
            // with a running download (PlayerApp gates with a timeout).
            try {
                startGate()
            } catch (_: Exception) {
            }
            val dao = db.downloadJobDao()
            val stuck = dao.resetStuck()
            if (stuck > 0) Log.i(TAG, "Re-queued $stuck interrupted job(s)")
            // Self-heal the 2046-row pileup: one queued row per URL survives
            // (earliest first), the rest are cancelled, never downloaded twice.
            val seen = HashSet<String>()
            var collapsed = 0
            for (row in dao.queued()) {
                val key = (row.url ?: "").trim()
                if (key.isEmpty() || !seen.add(key)) {
                    if (key.isNotEmpty()) {
                        dao.updateState(row.qid, "cancelled", row.progress, "Duplicate of an earlier queued job", null, now())
                        collapsed++
                    }
                }
            }
            if (collapsed > 0) Log.i(TAG, "Collapsed $collapsed duplicate queued job(s)")
            val limit = (db.settingsDao().get()?.limitDownloads ?: 3).coerceIn(1, 20)
            semaphore = Semaphore(limit)
            refresh()
            pump()
        }
    }

    /** Desktop submit(): empty url falls back to title+artist+audio search. */
    suspend fun submit(url: String, title: String, artist: String?, isPodcast: Boolean = false): String {
        var target = url.trim()
        if (target.isEmpty()) target = DownloadEngine.searchQuery(title, artist)
        // Dedupe in-flight URLs so repeated pulls can't stack duplicates.
        // Completed jobs do NOT block: deleting a file must allow re-download.
        db.downloadJobDao().activeByUrl(target)?.let { return it.qid }
        require(target.isNotEmpty()) { "Download failed: empty search or url" }
        val qid = UUID.randomUUID().toString().replace("-", "")
        db.downloadJobDao().insert(
            DownloadJobEntity(
                qid = qid, url = target,
                title = title.ifBlank { target }, artist = artist,
                status = "queued", progress = 0f,
                createdAt = now(), updatedAt = now(),
                isPodcast = if (isPodcast) 1 else 0
            )
        )
        refresh()
        pump()
        return qid
    }

    /** Desktop retry(): re-enqueue from the stored url/title/artist. */
    suspend fun retry(qid: String): Boolean {
        val row = db.downloadJobDao().byQid(qid) ?: return false
        val url = row.url?.trim().orEmpty()
        if (url.isEmpty() && row.title.isNullOrBlank()) return false
        db.downloadJobDao().updateState(qid, "queued", 0f, null, null, now())
        refresh()
        pump()
        return true
    }

    /** Cancel applies to queued jobs instantly; running jobs get SIGKILLed. */
    suspend fun cancel(qid: String): Boolean {
        val row = db.downloadJobDao().byQid(qid) ?: return false
        if (row.status == "queued") {
            db.downloadJobDao().updateState(qid, "cancelled", row.progress, "Cancelled by user", null, now())
            refresh()
            return true
        }
        if (row.status == "downloading") {
            synchronized(userCancelled) { userCancelled.add(qid) }
            engine.cancelDownload(qid)
            refresh()
            return true
        }
        return false
    }

    suspend fun refresh() {
        _jobs.value = db.downloadJobDao().recent(200).map { r ->
            JobItem(r.qid, r.url, r.title ?: r.url ?: "", r.artist, r.status, normalizeProgress(r.progress), r.error, r.filename, r.isPodcast == 1)
        }
        _total.value = db.downloadJobDao().total()
        _activeCount.value = db.downloadJobDao().countActive()
    }

    suspend fun job(qid: String): JobItem? =
        db.downloadJobDao().byQid(qid)?.let { r ->
            JobItem(r.qid, r.url, r.title ?: r.url ?: "", r.artist, r.status, normalizeProgress(r.progress), r.error, r.filename, r.isPodcast == 1)
        }

    /** Latest job for a URL, any status (lets sync skip already-fetched rows). */
    suspend fun findLatest(url: String): JobItem? =
        db.downloadJobDao().latestByUrl(url)?.let { r ->
            JobItem(r.qid, r.url, r.title ?: r.url ?: "", r.artist, r.status, normalizeProgress(r.progress), r.error, r.filename, r.isPodcast == 1)
        }

    /** Delete finished rows (housekeeping for the job log). Returns count. */
    suspend fun clearCompleted(): Int {
        val n = db.downloadJobDao().deleteTerminal()
        refresh()
        return n
    }

    fun setLimit(n: Int) {
        semaphore = Semaphore(n.coerceIn(1, 20))
        scope.launch { pump() }
    }

    private suspend fun pump() = pumpMutex.withLock {
        val queued = db.downloadJobDao().queued()
        for (row in queued) {
            // Claim synchronously so a second pump can't double-start it.
            db.downloadJobDao().updateState(row.qid, "downloading", 0f, null, null, now())
            scope.launch {
                semaphore.withPermit { runJob(row.qid) }
            }
        }
        // Keep the process (and network) alive under lock/Doze while active.
        if (queued.isNotEmpty()) DownloadService.ensure(appContext)
        refresh()
    }

    private suspend fun runJob(qid: String) {
        val dao = db.downloadJobDao()
        var row = dao.byQid(qid) ?: return
        if (row.status == "cancelled") {
            refresh()
            return
        }
        val target = row.url.orEmpty()
        var lastPush = 0L
        var lastVal = -1f
        var lastActivity = System.currentTimeMillis()
        var watchdogFired = false
        // Stuck watchdog: a yt-dlp run silent for 5 minutes gets killed and
        // named as failed instead of sitting at 0% forever.
        val watch = scope.launch {
            while (true) {
                kotlinx.coroutines.delay(60_000)
                if (System.currentTimeMillis() - lastActivity > 5 * 60_000) {
                    watchdogFired = true
                    engine.cancelDownload(qid)
                    break
                }
            }
        }
        try {
            // Podcasts folder routing (desktop is_podcast): songs->songs
            // folder, podcasts->podcasts folder.
            val isPod = row.isPodcast == 1
            val outDir = if (isPod) engine.podcastsDir() else engine.outputDir()
            val result = engine.downloadToMp3(
                target,
                onProgress = { frac ->
                    // Desktop scales download progress into 0..90%; stored 0..1.
                    // Clamp: yt-dlp sometimes reports >100% (frac > 1).
                    val scaled = (frac.coerceIn(0f, 1f) * 0.9f).coerceIn(0f, 0.9f)
                    lastActivity = System.currentTimeMillis()
                    val t = lastActivity
                    // Relaxed throttle (5% / 2s): each push is a DB write +
                    // a 3-query refresh + a full list recomposition, so the
                    // old 2%/1s cadence x3 concurrent jobs was pure churn.
                    if (scaled - lastVal >= 0.05f || t - lastPush > 2000) {
                        lastVal = scaled
                        lastPush = t
                        scope.launch {
                            dao.updateState(qid, "downloading", scaled, null, null, now())
                            refresh()
                        }
                    }
                },
                processId = qid,
                outDir = outDir
            )
            if (result.isSuccess) {
                val file = result.getOrThrow()
                // Desktop download_song tail: iTunes lookup -> apply_metadata
                // into the file (best-effort here: a tag failure must not fail
                // a good download the way desktop's raise does).
                // The downloaded filename already carries "id_Title", so derive
                // the tag title from it — avoids spawning a second yt-dlp
                // process (getInfo) for every URL download, which is pure
                // overhead (network + CPU) on top of the transcode.
                // Podcasts skip the songs-folder tagger (tags land at play
                // time via details()); only the Podcasts row is written.
                val title = row.title?.takeIf { it.isNotBlank() && it != target }
                    ?: file.nameWithoutExtension
                val artist = row.artist
                if (isPod) {
                    db.podcastDao().insertIgnore(
                        com.musicplayer.android.data.local.PodcastEntity(
                            file = file.name, downloadedLink = target,
                            title = title, dateDownload = now(), artist = artist
                        )
                    )
                    // Desktop apply_metadata for podcasts: keep the
                    // uploader's own title/author in the file tags (no
                    // iTunes song-match — it mangles episode names — and no
                    // artwork fetch). Best-effort like the song path.
                    try {
                        metadata?.updateMeta(
                            fileName = file.name,
                            title = title,
                            artist = artist ?: "",
                            album = "Unknown",
                            year = "Unknown",
                            removeCover = false,
                            isPodcast = true
                        )
                    } catch (_: Exception) {
                    }
                } else {
                    val effTitle = row.title?.takeIf { it.isNotBlank() && it != target }
                        ?: metadata?.fallbackTitle(file.name)?.takeIf { it.isNotBlank() }
                    autoTag(file.name, target, effTitle, row.artist)
                    // Desktop _save_song_record(): upsert Songs with fresh timestamp.
                    db.songDao().insertIgnore(
                        SongEntity(file.name, target, title, now(), artist)
                    )
                    // insertIgnore skips existing rows; desktop UPDATES them instead.
                    db.songDao().getByFile(file.name)?.let { existing ->
                        if (existing.title != title || existing.artist != artist ||
                            existing.downloadedLink != target
                        ) {
                            db.songDao().updateTitleArtist(file.name, title, artist ?: existing.artist ?: "")
                            // Keep the fresh link/timestamp like desktop's upsert.
                            db.songDao().updateDownloadedLink(file.name, target, now())
                        }
                    }
                }
                dao.updateState(qid, "done", 1f, null, file.name, now())
            } else {
                val err = result.exceptionOrNull()?.message ?: "Download failed"
                dao.updateState(qid, "failed", lastVal.coerceIn(0f, 1f), err, null, now())
            }
        } catch (e: Exception) {
            val wasCancelled = synchronized(userCancelled) { userCancelled.remove(qid) }
            if (wasCancelled) {
                dao.updateState(qid, "cancelled", 0f, "Cancelled by user", null, now())
            } else if (watchdogFired) {
                Log.e(TAG, "job $qid killed: no progress for 5 minutes")
                dao.updateState(qid, "failed", lastVal.coerceIn(0f, 1f), "Stopped: no progress for 5 minutes (network/YouTube blocked?)", null, now())
            } else {
                Log.e(TAG, "job $qid failed", e)
                dao.updateState(qid, "failed", lastVal.coerceIn(0f, 1f), "Download failed: ${e.message}", null, now())
            }
        } finally {
            watch.cancel()
        }
        refresh()
        pump()
        if (dao.countActive() == 0) {
            try {
                onIdle?.invoke()
            } catch (e: Exception) {
                Log.w(TAG, "idle callback error: ${e.message}")
            }
        }
    }

    private fun now(): String = STAMP.format(Date())

    /**
     * Desktop download_song() tail: resolve a proper title/artist (video info
     * when the job has none), iTunes lookup, write tags + cover into the mp3.
     * Best-effort: any failure is logged and the download still counts.
     */
    private suspend fun autoTag(fileName: String, target: String, jobTitle: String?, jobArtist: String?) {
        val meta = metadata ?: return
        val art = artwork ?: return
        try {
            var title = jobTitle?.takeIf { it.isNotBlank() && it != target }
            var artist = jobArtist?.takeIf { it.isNotBlank() }
            if (title == null) {
                // Ask yt-dlp for the video title/uploader (desktop info dict).
                try {
                    val info = com.yausername.youtubedl_android.YoutubeDL.getInstance().getInfo(target)
                    title = info.title?.takeIf { it.isNotBlank() }
                    if (artist == null) artist = info.uploader?.takeIf { it.isNotBlank() }
                } catch (_: Exception) {
                }
                if (title == null) {
                    title = java.io.File(fileName).nameWithoutExtension
                }
            }
            val hits = art.searchSongs(title, artist ?: "")
            val hit = hits.firstOrNull()
            val finalTitle = hit?.title?.takeIf { it.isNotBlank() } ?: title
            val finalArtist = hit?.artist?.takeIf { it.isNotBlank() }
                ?: artist?.takeIf { it.isNotBlank() } ?: "Unknown"
            // Cover first so tags+artwork land in ONE file rewrite via
            // updateMetaWithCover(), not updateMeta()+setCoverBytes() back
            // to back (two full rewrites + two library refreshes per song).
            val cover = if (hit != null && hit.artworkUrl.isNotBlank()) {
                art.fetchBytes(hit.artworkUrl)
            } else null
            val ok = meta.updateMetaWithCover(
                fileName = fileName,
                title = finalTitle,
                artist = finalArtist,
                album = hit?.album ?: "Unknown",
                year = hit?.year ?: "Unknown",
                genre = hit?.genre,
                cover = cover
            )
            Log.i(TAG, "auto-tag ${if (ok) "ok" else "failed"}: $fileName <- $finalTitle / $finalArtist")
        } catch (e: Exception) {
            Log.w(TAG, "auto-tag skipped for $fileName: ${e.message}")
        }
    }

    companion object {
        private const val TAG = "QueueRepository"
        private val STAMP = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }

        /**
         * progress01 is 0..1. Older installs stored 0..100 (done=100),
         * so heal on read: anything >1 is treated as percent.
         */
        fun normalizeProgress(p: Float): Float = when {
            !p.isFinite() -> 0f
            p > 1f -> (p / 100f).coerceIn(0f, 1f)
            else -> p.coerceIn(0f, 1f)
        }
    }
}
