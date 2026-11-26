package com.musicplayer.android

import android.app.Application
import android.util.Log
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.QueueRepository
import com.musicplayer.android.data.SyncRepository
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.update.UpdateRepository
import com.musicplayer.android.engine.DownloadEngine
import com.musicplayer.android.playback.AiDjController
import com.musicplayer.android.playback.PlaybackTelemetry
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.playback.PreviewPlayer
import com.yausername.ffmpeg.FFmpeg
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Phase 1: initialise the yt-dlp + ffmpeg engine once, surface the result so
 * the UI can show a real error instead of a silent failure.
 *
 * The player + download engine live here (process lifetime) so the UI and the
 * foreground playback service share one instance.
 */
class PlayerApp : Application() {

    private val _engineState = MutableStateFlow<EngineState>(EngineState.Starting)
    val engineState: StateFlow<EngineState> = _engineState.asStateFlow()

    private val _downloaderStatus =
        MutableStateFlow<DownloaderStatus>(DownloaderStatus.Starting)
    val downloaderStatus: StateFlow<DownloaderStatus> = _downloaderStatus.asStateFlow()
    private val downloaderMutex = Mutex()

    lateinit var downloadEngine: DownloadEngine
        private set
    lateinit var player: PlayerManager
        private set
    // Discover playback-quality telemetry (one segment per music track).
    private lateinit var playbackTelemetry: PlaybackTelemetry
    // App-scoped so Vohithe keeps talking across navigation/screen rotation.
    lateinit var aiDjController: AiDjController
        private set
    // Stream previews for download search results (independent of the queue).
    lateinit var previewPlayer: PreviewPlayer
        private set
    // App-scoped so downloads/sync survive the activity (background worker).
    lateinit var queueRepo: QueueRepository
        private set
    lateinit var syncRepo: SyncRepository
        private set
    /** App-scoped sideload updater (downloads survive activity recreation). */
    lateinit var updateRepo: UpdateRepository
        private set

    /**
     * Active music folder (desktop songs_path equivalent). Starts as the
     * app-private Music dir; Settings can move it to a user-picked shared
     * folder (SAF) and it is restored from the Settings row on launch.
     */
    lateinit var musicDir: java.io.File
        private set

    /**
     * Second library dir (desktop podcasts_path equivalent). Podcasts pull
     * downloads land here, never in [musicDir].
     */
    lateinit var podcastsDir: java.io.File
        private set

    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    /**
     * yt-dlp self-update (desktop _check_update_ytdlp_once, adapted: the
     * library's STABLE channel instead of pip). Fire-and-forget on every
     * launch; the queue waits for it (with timeout) so a binary swap can
     * never collide with a running download.
     */
    lateinit var ytdlpUpdate: Job
        private set

    override fun onCreate() {
        super.onCreate()
        instance = this
        SyncConfig.init(this)
        updateRepo = UpdateRepository(this)
        musicDir = java.io.File(
            getExternalFilesDir(android.os.Environment.DIRECTORY_MUSIC),
            "AndroidPlayer"
        )
        if (!musicDir.exists()) musicDir.mkdirs()
        podcastsDir = java.io.File(
            getExternalFilesDir(android.os.Environment.DIRECTORY_PODCASTS),
            "AndroidPlayer"
        )
        if (!podcastsDir.exists()) podcastsDir.mkdirs()
        downloadEngine = DownloadEngine(this)
        player = PlayerManager(this) { downloadEngine.outputDir() }
        player.isPodcastFile = { f ->
            try {
                f.canonicalPath.startsWith(podcastsDir.canonicalPath)
            } catch (_: Exception) {
                false
            }
        }
        playbackTelemetry = PlaybackTelemetry()
        player.playbackObserver = playbackTelemetry
        previewPlayer = PreviewPlayer(this)
        aiDjController = AiDjController(this, player) { name ->
            java.io.File(downloadEngine.outputDir(), name)
        }
        // ID3 writes default to ISO-8859-1, which mangles anything outside
        // Latin-1 (é, –, CJK…). v2.4 UTF-8 is readable everywhere (Android,
        // desktop mutagen) and ASCII-identical, so force it for new frames.
        try {
            org.jaudiotagger.tag.TagOptionSingleton.getInstance().setId3v24DefaultTextEncoding(
                org.jaudiotagger.tag.id3.valuepair.TextEncoding.UTF_8
            )
        } catch (e: Exception) {
            Log.w(TAG, "Could not force UTF-8 ID3 encoding: ${e.message}")
        }
        // Metadata/artwork repos feed the queue's auto-tagger (built first).
        val metadataRepo = MetadataRepository(this, downloadEngine)
        val artworkRepo = ArtworkRepository()
        try {
            YoutubeDL.getInstance().init(this)
            FFmpeg.getInstance().init(this) // required for -x / --audio-format
            _engineState.value = EngineState.Ready(
                YoutubeDL.getInstance().version(this) ?: "unknown"
            )
            Log.i(TAG, "yt-dlp engine ready")
        } catch (e: YoutubeDLException) {
            Log.e(TAG, "Failed to initialize youtubedl-android", e)
            val reason = e.message ?: e.toString()
            _engineState.value = EngineState.Failed(reason)
            _downloaderStatus.value = DownloaderStatus.Failed(null, reason)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize youtubedl-android", e)
            val reason = e.message ?: e.toString()
            _engineState.value = EngineState.Failed(reason)
            _downloaderStatus.value = DownloaderStatus.Failed(null, reason)
        }
        ytdlpUpdate = appScope.launch {
            downloaderMutex.withLock { updateDownloaderLocked() }
        }
        queueRepo = QueueRepository(
            this,
            downloadEngine,
            metadata = metadataRepo,
            artwork = artworkRepo,
            startGate = {
                // Gate the first pump on any updater run (max 3 min, then proceed).
                withTimeoutOrNull(180_000) {
                    ytdlpUpdate.join()
                    downloaderMutex.withLock { }
                }
            }
        )
        syncRepo = SyncRepository(this, downloadEngine)
        syncRepo.scheduleHistoryPush()
    }

    sealed interface EngineState {
        data object Starting : EngineState
        data class Ready(val ytDlpVersion: String) : EngineState
        data class Failed(val reason: String) : EngineState
    }

    sealed interface DownloaderStatus {
        data object Starting : DownloaderStatus
        data class Updating(val version: String?) : DownloaderStatus
        data class Ready(val version: String?, val message: String) : DownloaderStatus
        data class Failed(val version: String?, val message: String) : DownloaderStatus
    }

    /** Manual Settings action; joins an in-flight startup update instead of stacking one. */
    fun updateDownloaderNow() {
        if (::ytdlpUpdate.isInitialized && ytdlpUpdate.isActive) {
            appScope.launch { ytdlpUpdate.join() }
            return
        }
        appScope.launch {
            downloaderMutex.withLock { updateDownloaderLocked() }
        }
    }

    private suspend fun updateDownloaderLocked() {
        val before = try {
            YoutubeDL.getInstance().version(this)
        } catch (_: Exception) {
            null
        }
        _downloaderStatus.value = DownloaderStatus.Updating(before)
        try {
            val result = YoutubeDL.getInstance().updateYoutubeDL(
                this,
                YoutubeDL.UpdateChannel._STABLE
            )
            val after = try {
                YoutubeDL.getInstance().version(this)
            } catch (_: Exception) {
                before
            }
            if (after != null) _engineState.value = EngineState.Ready(after)
            val message = if (before != null && after != null && before != after) {
                "Updated from $before."
            } else {
                "Already up to date."
            }
            _downloaderStatus.value = DownloaderStatus.Ready(after, message)
            Log.i(TAG, "yt-dlp update: $result (now $after)")
        } catch (e: Exception) {
            val message = e.message ?: "Update failed."
            _downloaderStatus.value = DownloaderStatus.Failed(before, message)
            Log.w(TAG, "yt-dlp update failed (keeping bundled): $message")
        }
    }

    /** Switch the active music folder (Settings change-folder flow). */
    fun setMusicDir(dir: java.io.File) {
        musicDir = dir
    }

    /** Switch the active podcasts folder (desktop podcasts_path). */
    fun setPodcastsDir(dir: java.io.File) {
        podcastsDir = dir
    }

    companion object {
        private const val TAG = "PlayerApp"
        lateinit var instance: PlayerApp
            private set
    }
}
