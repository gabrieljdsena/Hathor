package com.musicplayer.android.playback

import android.content.Context
import android.util.Log
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Stream preview for download search results: resolves the direct audio URL
 * (no download, no transcode) and plays it on a throwaway ExoPlayer that is
 * fully independent of the main playback queue — previewing never disturbs
 * what (if anything) is currently playing, and starting main playback stops
 * any preview (wired in MainActivity).
 *
 * ExoPlayer (not MediaPlayer): stream URLs can be any best-audio codec
 * incl. opus-in-webm, which MediaPlayer cannot play — that was the "works
 * for some songs" gap.
 *
 * All player access happens on Dispatchers.Main (this class's scope);
 * only the URL resolve runs on IO.
 */
class PreviewPlayer(appContext: Context) {

    sealed interface State {
        data object Idle : State
        data class Loading(val id: String) : State
        data class Playing(val id: String) : State
        data class Failed(val id: String, val message: String) : State
    }

    private val appCtx = appContext.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state.asStateFlow()

    private var player: ExoPlayer? = null
    private var task: Job? = null
    private var readyWatchdog: Job? = null
    private var resolveEngine: DownloadEngine? = null
    private var resolveProcessId: String? = null

    /**
     * Toggle: tap the playing/loading row again to stop, tap another row to
     * switch. [onStarted] fires when audio actually starts (used to pause
     * the main player so both never play at once).
     */
    fun toggle(id: String, pageUrl: String, engine: DownloadEngine, onStarted: () -> Unit = {}) {
        when (val cur = _state.value) {
            is State.Playing -> if (cur.id == id) {
                stop()
                return
            }
            is State.Loading -> if (cur.id == id) {
                stop()
                return
            }
            else -> {}
        }
        stopInternal()
        resolveEngine = engine
        _state.value = State.Loading(id)
        task = scope.launch {
            val pid = "preview-$id-${System.currentTimeMillis()}"
            resolveProcessId = pid
            val res: Result<String> = withContext(Dispatchers.IO) {
                // Bounded resolve: yt-dlp can stall on bad networks, and a
                // stuck coroutine would leave the row spinning forever.
                val r = withTimeoutOrNull(RESOLVE_TIMEOUT_MS) {
                    engine.getAudioStreamUrl(pageUrl, pid)
                }
                if (r == null) {
                    // Timeout cancels the coroutine but NOT the native
                    // process — kill it explicitly so resolves can't pile up.
                    try {
                        engine.cancelDownload(pid)
                    } catch (_: Exception) {
                    }
                    Result.failure(IllegalStateException("Timed out resolving stream"))
                } else r
            }
            resolveProcessId = null
            if (!isActive) return@launch
            res.fold(
                onSuccess = { stream ->
                    startStream(id, stream)
                    onStarted()
                },
                onFailure = { e ->
                    // Cancelled by stop()/switch: stay silent, state is Idle.
                    if (e !is CancellationException) {
                        _state.value = State.Failed(id, e.message ?: e.toString())
                    }
                }
            )
        }
    }

    fun stop() {
        readyWatchdog?.cancel()
        readyWatchdog = null
        task?.cancel()
        task = null
        stopInternal()
        _state.value = State.Idle
    }

    fun release() {
        stop()
        scope.cancel()
    }

    private fun startStream(id: String, streamUrl: String) {
        stopPlayer()
        try {
            val exo = ExoPlayer.Builder(appCtx).build()
            exo.addListener(object : Player.Listener {
                override fun onPlaybackStateChanged(playbackState: Int) {
                    if (playbackState == Player.STATE_READY) {
                        readyWatchdog?.cancel()
                        readyWatchdog = null
                        if ((_state.value as? State.Loading)?.id == id) {
                            _state.value = State.Playing(id)
                        }
                    } else if (playbackState == Player.STATE_ENDED) {
                        stop()
                    }
                }

                override fun onPlayerError(error: PlaybackException) {
                    Log.w(TAG, "preview error: ${error.message}")
                    readyWatchdog?.cancel()
                    readyWatchdog = null
                    if ((_state.value as? State.Loading)?.id == id ||
                        (_state.value as? State.Playing)?.id == id
                    ) {
                        stopPlayer()
                        _state.value = State.Failed(id, error.message ?: "Playback error")
                    }
                }
            })
            player = exo
            exo.setMediaItem(MediaItem.fromUri(streamUrl))
            exo.prepare()
            exo.play()
            // Watchdog: a stream that never becomes ready (and never errors)
            // must not spin the row forever.
            readyWatchdog?.cancel()
            readyWatchdog = scope.launch {
                delay(READY_TIMEOUT_MS)
                if ((_state.value as? State.Loading)?.id == id) {
                    Log.w(TAG, "preview ready timed out")
                    stop()
                    _state.value = State.Failed(id, "Playback timed out")
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "preview failed: ${e.message}")
            _state.value = State.Failed(id, e.message ?: e.toString())
        }
    }

    private fun stopInternal() {
        // Kill an in-flight URL resolve (yt-dlp process) too.
        resolveProcessId?.let { pid ->
            try {
                resolveEngine?.cancelDownload(pid)
            } catch (_: Exception) {
            }
        }
        resolveProcessId = null
        resolveEngine = null
        task?.cancel()
        task = null
        readyWatchdog?.cancel()
        readyWatchdog = null
        stopPlayer()
    }

    private fun stopPlayer() {
        val exo = player
        player = null
        if (exo == null) return
        try {
            exo.stop()
        } catch (_: Exception) {
        }
        try {
            exo.release()
        } catch (_: Exception) {
        }
    }

    companion object {
        private const val TAG = "PreviewPlayer"
        private const val RESOLVE_TIMEOUT_MS = 30_000L
        private const val READY_TIMEOUT_MS = 15_000L
    }
}
