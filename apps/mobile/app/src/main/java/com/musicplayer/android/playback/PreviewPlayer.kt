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

    /** Metadata shown by the cross-tab preview dock. */
    data class PreviewInfo(
        val id: String,
        val title: String,
        val artist: String,
        val artworkUrl: String
    )

    private val appCtx = appContext.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state.asStateFlow()

    private val _info = MutableStateFlow<PreviewInfo?>(null)
    val info: StateFlow<PreviewInfo?> = _info.asStateFlow()

    private val _positionMs = MutableStateFlow(0L)
    val positionMs: StateFlow<Long> = _positionMs.asStateFlow()

    private val _durationMs = MutableStateFlow(0L)
    val durationMs: StateFlow<Long> = _durationMs.asStateFlow()

    private var player: ExoPlayer? = null
    private var task: Job? = null
    private var readyWatchdog: Job? = null
    private var progressTicker: Job? = null
    private var resolveEngine: DownloadEngine? = null
    private var resolveProcessId: String? = null

    /**
     * Toggle: tap the playing/loading row again to stop, tap another row to
     * switch. [onStarted] fires when audio actually starts (used to pause
     * the main player so both never play at once).
     */
    fun toggle(
        id: String,
        pageUrl: String,
        engine: DownloadEngine,
        onStarted: () -> Unit = {},
        stateId: String = id,
        info: PreviewInfo? = null
    ) {
        when (val cur = _state.value) {
            is State.Playing -> if (cur.id == stateId) {
                stop()
                return
            }
            is State.Loading -> if (cur.id == stateId) {
                stop()
                return
            }
            else -> {}
        }
        stopInternal()
        _info.value = info
        resolveEngine = engine
        _state.value = State.Loading(stateId)
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
                    startStream(stateId, stream)
                    onStarted()
                },
                onFailure = { e ->
                    // Cancelled by stop()/switch: stay silent, state is Idle.
                    if (e !is CancellationException) {
                        _state.value = State.Failed(stateId, e.message ?: e.toString())
                    }
                }
            )
        }
    }

    /**
     * Play a stream URL resolved elsewhere (the Web API preview endpoint).
     * [stateId] is the caller's row/card key; it need not be the video id.
     */
    fun playResolved(stateId: String, audioUrl: String, info: PreviewInfo? = null) {
        when (val cur = _state.value) {
            is State.Playing -> if (cur.id == stateId) {
                stop()
                return
            }
            is State.Loading -> if (cur.id == stateId) {
                stop()
                return
            }
            else -> {}
        }
        stopInternal()
        _info.value = info
        _state.value = State.Loading(stateId)
        startStream(stateId, audioUrl)
    }

    /** Seek the active preview to a fraction of its duration (0..1). */
    fun seekToFraction(fraction: Float) {
        val exo = player ?: return
        val dur = try {
            exo.duration
        } catch (_: Exception) {
            0L
        }
        if (dur <= 0) return
        val target = (fraction.coerceIn(0f, 1f) * dur).toLong()
        try {
            exo.seekTo(target)
            _positionMs.value = target
            Log.i(TAG, "preview seek ${target}ms / ${dur}ms")
        } catch (_: Exception) {
        }
    }

    fun stop() {
        readyWatchdog?.cancel()
        readyWatchdog = null
        task?.cancel()
        task = null
        stopInternal()
        _state.value = State.Idle
        _info.value = null
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
            _positionMs.value = 0L
            _durationMs.value = 0L
            startProgressTicker()
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
        stopProgressTicker()
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

    private fun startProgressTicker() {
        stopProgressTicker()
        progressTicker = scope.launch {
            while (isActive) {
                val exo = player ?: break
                try {
                    if (exo.isPlaying) {
                        val pos = exo.currentPosition
                        if (pos >= 0) _positionMs.value = pos
                        val dur = exo.duration
                        if (dur > 0) _durationMs.value = dur
                    }
                } catch (_: Exception) {
                }
                delay(250)
            }
        }
    }

    private fun stopProgressTicker() {
        progressTicker?.cancel()
        progressTicker = null
        _positionMs.value = 0L
        _durationMs.value = 0L
    }

    companion object {
        private const val TAG = "PreviewPlayer"
        private const val RESOLVE_TIMEOUT_MS = 30_000L
        private const val READY_TIMEOUT_MS = 15_000L
    }
}
