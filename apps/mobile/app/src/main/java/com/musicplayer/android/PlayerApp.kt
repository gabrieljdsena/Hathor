package com.musicplayer.android

import android.app.Application
import android.util.Log
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.QueueRepository
import com.musicplayer.android.data.SyncRepository
import com.musicplayer.android.engine.DownloadEngine
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

    lateinit var downloadEngine: DownloadEngine
        private set
    lateinit var player: PlayerManager
        private set
    // Stream previews for download search results (independent of the queue).
    lateinit var previewPlayer: PreviewPlayer
        private set
    // App-scoped so downloads/sync survive the activity (background worker).
    lateinit var queueRepo: QueueRepository
        private set
    lateinit var syncRepo: SyncRepository
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
        previewPlayer = PreviewPlayer(this)
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
            _engineState.value = EngineState.Failed(e.message ?: e.toString())
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize youtubedl-android", e)
            _engineState.value = EngineState.Failed(e.message ?: e.toString())
        }
        ytdlpUpdate = appScope.launch {
            try {
                val status = YoutubeDL.getInstance().updateYoutubeDL(
                    this@PlayerApp,
                    YoutubeDL.UpdateChannel._STABLE
                )
                val ver = try {
                    YoutubeDL.getInstance().version(this@PlayerApp)
                } catch (_: Exception) {
                    null
                }
                Log.i(TAG, "yt-dlp auto-update: $status (now $ver)")
            } catch (e: Exception) {
                Log.w(TAG, "yt-dlp auto-update failed (keeping bundled): ${e.message}")
            }
        }
        queueRepo = QueueRepository(
            this,
            downloadEngine,
            metadata = metadataRepo,
            artwork = artworkRepo,
            startGate = {
                // Gate the first pump on the updater (max 3 min, then proceed).
                withTimeoutOrNull(180_000) { ytdlpUpdate.join() }
            }
        )
        syncRepo = SyncRepository(this, downloadEngine)
    }

    sealed interface EngineState {
        data object Starting : EngineState
        data class Ready(val ytDlpVersion: String) : EngineState
        data class Failed(val reason: String) : EngineState
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
