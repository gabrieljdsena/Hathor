package com.musicplayer.android.playback

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.api.AiDjApi
import com.musicplayer.android.data.api.AiDjBatch
import com.musicplayer.android.data.api.AiDjState
import com.musicplayer.android.data.api.PlayerControlApi
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncConfig
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.max

enum class DjVoiceStatus { Idle, Speaking, Waiting }

data class AiDjUiState(
    val loading: Boolean = true,
    val busy: Boolean = false,
    val server: AiDjState? = null,
    val active: Boolean = false,
    val voice: DjVoiceStatus = DjVoiceStatus.Idle,
    val currentBatch: AiDjBatch? = null,
    val nextBatch: AiDjBatch? = null,
    val error: String? = null
)

/**
 * App-scoped AI DJ session. Mirrors the web client's contract:
 * - start/stop/feedback through /ai-dj
 * - queue and track changes through /player/queue, /player/play, /player/next
 * - Vohithe's clips play over ducked music, intro at set start and
 *   outro + next intro at the batch boundary.
 *
 * The phone keeps playing its own synced files; the Web API remains the source
 * of truth for batches and commentary.
 */
class AiDjController(
    appContext: Context,
    private val player: PlayerManager,
    private val resolveFile: (String) -> File
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val voice = DjVoicePlayer(appContext)
    private val _ui = MutableStateFlow(AiDjUiState())
    val ui: StateFlow<AiDjUiState> = _ui.asStateFlow()

    /** Notifies the app when the persisted playback source should change. */
    var onSessionChanged: ((String?) -> Unit)? = null

    private var pollJob: Job? = null
    private var waitJob: Job? = null
    private var sessionId: String? = null
    private var localBatch: AiDjBatch? = null
    private var nextBatch: AiDjBatch? = null
    private var localFiles: List<File> = emptyList()
    private var expectedPaths: List<String> = emptyList()
    private var boundaryStarted = false
    private var boundaryIntroPlayed = false
    private var waitingForVoice = false
    private var waitingAtEnd = false
    private var advancing = false
    private var lastServerFile: String? = null
    private val nextMutex = Mutex()

    init {
        player.onQueueExhausted = { onQueueExhausted() }
        // Every local track change also advances the server queue (web parity):
        // that is what rotates currentBatch and keeps prefetch alive.
        scope.launch {
            player.current.collect { file -> onLocalCurrentChanged(file) }
        }
        scope.launch {
            voice.speaking.collect { speaking ->
                _ui.update {
                    it.copy(
                        voice = when {
                            speaking -> DjVoiceStatus.Speaking
                            waitingForVoice -> DjVoiceStatus.Waiting
                            else -> DjVoiceStatus.Idle
                        }
                    )
                }
            }
        }
        // Boundary voice starts a few seconds before the last track ends.
        scope.launch {
            while (isActive) {
                try {
                    maybeStartBoundaryVoice()
                } catch (e: Exception) {
                    Log.w(TAG, "boundary check failed: ${e.message}")
                }
                delay(400)
            }
        }
    }

    fun refresh() {
        scope.launch {
            try {
                val state = AiDjApi.state()
                applyServerState(state)
            } catch (e: Exception) {
                if (!_ui.value.active) {
                    _ui.update {
                        it.copy(
                            loading = false,
                            error = e.message ?: "Could not reach the AI DJ."
                        )
                    }
                }
            }
        }
    }

    fun start(brief: String) {
        if (_ui.value.busy) return
        _ui.update { it.copy(busy = true, loading = false, error = null, active = false) }
        resetLocal(keepUiServer = true)
        scope.launch {
            var startedSession: String? = null
            try {
                // DJ sets are linear; repeat-one would trap the boundary.
                if (player.repeatFlow.value) player.toggleRepeat()
                val state = AiDjApi.start(brief.trim())
                Log.i(TAG, "start: session=${state.sessionId} batch=${state.currentBatch?.number}")
                startedSession = state.sessionId
                if (!state.enabled) {
                    throw IllegalStateException("AI DJ is disabled on the server.")
                }
                if (!state.configured) {
                    throw IllegalStateException("AI DJ is enabled but not configured on the server.")
                }
                val batch = state.currentBatch
                    ?: throw IllegalStateException("The DJ did not return a playable first set.")
                if (state.sessionId.isNullOrBlank()) {
                    throw IllegalStateException("The DJ did not return a session.")
                }
                val files = resolveBatchWithDownloads(batch)
                if (files.isEmpty()) {
                    throw IllegalStateException(
                        "The DJ selected tracks that are not downloaded on this device. Pull the library first."
                    )
                }
                sessionId = state.sessionId
                localBatch = batch
                nextBatch = state.nextBatch
                localFiles = files
                expectedPaths = files.map { it.absolutePath }
                onSessionChanged?.invoke(state.sessionId)
                _ui.update {
                    it.copy(
                        busy = false,
                        server = state,
                        active = true,
                        voice = DjVoiceStatus.Idle,
                        currentBatch = batch,
                        nextBatch = state.nextBatch,
                        error = null
                    )
                }
                // Keep the server queue/source in sync so prefetch can append
                // the following batches. Best effort: local playback still works.
                scope.launch(Dispatchers.IO) {
                    try {
                        PlayerControlApi.setDjQueue(
                            currentFile = files.first().name,
                            files = files.map { it.name },
                            sessionId = state.sessionId
                        )
                        PlayerControlApi.play(files.first().name)
                    } catch (e: Exception) {
                        Log.w(TAG, "server queue sync failed: ${e.message}")
                    }
                }
                lastServerFile = files.first().name
                player.playFiles(files, 0)
                startPolling()
                if (batch.shouldComment && batch.introClipIds.isNotEmpty()) {
                    delay(250)
                    playIntro(batch)
                }
            } catch (e: Exception) {
                Log.w(TAG, "start failed: ${e.message}", e)
                if (startedSession != null) {
                    withContext(Dispatchers.IO) {
                        try {
                            AiDjApi.stop()
                        } catch (_: Exception) {
                        }
                    }
                }
                resetLocal(keepUiServer = true)
                _ui.update {
                    it.copy(
                        busy = false,
                        loading = false,
                        active = false,
                        currentBatch = null,
                        nextBatch = null,
                        error = e.message ?: "Could not start the AI DJ."
                    )
                }
                onSessionChanged?.invoke(null)
            }
        }
    }

    fun stop() {
        val hadSession = _ui.value.server?.sessionId != null || localBatch != null
        resetLocal(keepUiServer = true)
        onSessionChanged?.invoke(null)
        _ui.update {
            it.copy(
                busy = true,
                active = false,
                voice = DjVoiceStatus.Idle,
                error = null
            )
        }
        scope.launch {
            try {
                val state = if (hadSession) AiDjApi.stop() else AiDjApi.state()
                _ui.update {
                    it.copy(
                        busy = false,
                        server = state,
                        currentBatch = state.currentBatch,
                        nextBatch = state.nextBatch,
                        loading = false
                    )
                }
            } catch (e: Exception) {
                _ui.update {
                    it.copy(
                        busy = false,
                        error = e.message ?: "Could not stop the AI DJ."
                    )
                }
            }
        }
    }

    fun feedback(event: String) {
        val batch = localBatch ?: _ui.value.server?.currentBatch ?: return
        val session = sessionId ?: _ui.value.server?.sessionId ?: return
        scope.launch(Dispatchers.IO) {
            try {
                AiDjApi.feedback(session, batch.id, null, event)
            } catch (_: Exception) {
            }
        }
    }

    /**
     * Called by the app's queue persistence hook. Any queue that is not the
     * one this DJ session installed means the user started something else;
     * end DJ mode like the web client does.
     */
    fun onQueueChanged(files: List<File>) {
        if (!localBatchFilesMatch(files)) {
            if (_ui.value.active) {
                Log.i(TAG, "external queue started; ending AI DJ session (${files.size} files)")
                resetLocal(keepUiServer = true)
                onSessionChanged?.invoke(null)
                _ui.update { it.copy(active = false, voice = DjVoiceStatus.Idle) }
                scope.launch(Dispatchers.IO) {
                    try {
                        AiDjApi.stop()
                    } catch (_: Exception) {
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Session internals
    // ------------------------------------------------------------------

    private fun localBatchFilesMatch(files: List<File>): Boolean {
        if (!_ui.value.active) return true
        if (files.isEmpty()) return false
        return files.map { it.absolutePath } == expectedPaths
    }

    /**
     * The server queue mirrors the local DJ queue one track at a time. The
     * expected-file guard makes this idempotent if a poll/crossfade raced it.
     */
    private fun onLocalCurrentChanged(file: File?) {
        val previous = lastServerFile
        lastServerFile = file?.name
        if (!_ui.value.active || file == null || previous == null) return
        if (previous == file.name) return
        val oldIndex = localFiles.indexOfFirst { it.name == previous }
        val newIndex = localFiles.indexOfFirst { it.name == file.name }
        if (newIndex < 0) return
        // Backwards (prev / queue jump) must not advance the server queue.
        if (oldIndex >= 0 && newIndex <= oldIndex) return
        scope.launch(Dispatchers.IO) {
            nextMutex.withLock {
                try {
                    Log.d(TAG, "server next expected=$previous")
                    PlayerControlApi.next(auto = true, expectedFile = previous)
                } catch (e: Exception) {
                    Log.w(TAG, "server next failed: ${e.message}")
                }
            }
        }
    }

    private fun resolveBatch(batch: AiDjBatch): List<File> =
        batch.tracks.mapNotNull { track ->
            val file = try {
                resolveFile(track.file)
            } catch (_: Exception) {
                null
            }
            file?.takeIf { it.exists() }
        }

    /**
     * Start can fill a gap automatically: the DJ's first batch is small, and
     * a partial library pull should not make the whole feature unusable.
     * Subsequent batches still expect the normal sync to be in place.
     */
    private suspend fun resolveBatchWithDownloads(batch: AiDjBatch): List<File> =
        withContext(Dispatchers.IO) {
            val api = if (SyncConfig.isConfigured()) {
                SyncApi(SyncConfig.baseUrl(), SyncConfig.token())
            } else {
                null
            }
            batch.tracks.mapNotNull { track ->
                val file = try {
                    resolveFile(track.file)
                } catch (_: Exception) {
                    null
                } ?: return@mapNotNull null
                if (file.exists()) return@mapNotNull file
                if (api == null) return@mapNotNull null
                try {
                    if (api.downloadFile(track.file, "songs", file)) {
                        Log.i(TAG, "downloaded missing DJ track ${track.file}")
                        file
                    } else {
                        null
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "DJ track download failed ${track.file}: ${e.message}")
                    null
                }
            }
        }

    private fun playIntro(batch: AiDjBatch) {
        if (!_ui.value.active) return
        val ids = batch.introClipIds
        if (ids.isEmpty()) return
        player.setDucked(true, duckLevel())
        voice.play(ids) {
            if (_ui.value.active) player.setDucked(false)
        }
    }

    private fun maybeStartBoundaryVoice() {
        if (!_ui.value.active) return
        if (boundaryStarted) {
            // While still playing, retry a transition that was not ready yet.
            if (waitingForVoice && player.playing.value) {
                tryStartBoundaryVoice()
            }
            return
        }
        val current = player.current.value ?: return
        val last = localFiles.lastOrNull() ?: return
        if (current.absolutePath != last.absolutePath) return
        val dur = player.durationMs.value
        val pos = player.positionMs.value
        if (!djBoundaryDue(pos, dur, duckLeadSeconds())) return
        boundaryStarted = true
        if (!tryStartBoundaryVoice()) {
            waitingForVoice = true
            _ui.update { it.copy(voice = DjVoiceStatus.Waiting) }
        } else {
            waitingForVoice = false
        }
    }

    /**
     * Returns true when voice is now speaking (or already was). False means
     * the clips are not ready yet.
     */
    private fun tryStartBoundaryVoice(): Boolean {
        val current = localBatch ?: return false
        val next = nextBatch
        if (voice.speaking.value) return true
        if (next == null) {
            waitingForVoice = true
            _ui.update { it.copy(voice = DjVoiceStatus.Waiting) }
            return false
        }
        val ids = current.outroClipIds + next.introClipIds
        if (ids.isEmpty()) {
            waitingForVoice = true
            _ui.update { it.copy(voice = DjVoiceStatus.Waiting) }
            return false
        }
        boundaryIntroPlayed = true
        waitingForVoice = false
        _ui.update { it.copy(voice = DjVoiceStatus.Speaking) }
        player.setDucked(true, duckLevel())
        voice.play(ids) {
            if (_ui.value.active) player.setDucked(false)
            if (waitingAtEnd) {
                waitingAtEnd = false
                waitJob?.cancel()
                waitJob = null
                beginNextBatch()
            }
        }
        return true
    }

    private fun onQueueExhausted() {
        if (!_ui.value.active || advancing) return
        if (voice.speaking.value) {
            // Voice is already running over the transition: start music now,
            // still ducked until the clip set finishes.
            beginNextBatch()
            return
        }
        if (waitingForVoice) {
            waitAtEndForVoice()
            return
        }
        if (boundaryStarted) {
            beginNextBatch()
            return
        }
        // A very short track can reach the end before the lead window.
        boundaryStarted = true
        if (tryStartBoundaryVoice()) {
            if (voice.speaking.value) {
                // Voice just started at the boundary; let its onDone callback
                // advance so the outro/intro is not cut by the next track.
                waitingAtEnd = true
            } else {
                beginNextBatch()
            }
        } else {
            waitAtEndForVoice()
        }
    }

    private fun waitAtEndForVoice() {
        if (waitJob?.isActive == true) return
        waitingAtEnd = true
        waitingForVoice = true
        _ui.update { it.copy(voice = DjVoiceStatus.Waiting) }
        waitJob = scope.launch {
            val deadline = System.currentTimeMillis() + 30_000
            while (isActive && _ui.value.active && System.currentTimeMillis() < deadline) {
                requestStateNow()
                if (tryStartBoundaryVoice()) {
                    // The voice onDone callback starts the next batch.
                    return@launch
                }
                delay(1_500)
            }
            if (_ui.value.active && waitingForVoice) {
                Log.w(TAG, "voice not ready at boundary; advancing silently")
                waitingForVoice = false
                waitingAtEnd = false
                player.setDucked(false)
                _ui.update { it.copy(voice = DjVoiceStatus.Idle) }
                beginNextBatch()
            }
        }
    }

    private fun beginNextBatch() {
        if (advancing) return
        val next = nextBatch
        if (next == null) {
            _ui.update {
                it.copy(error = "Waiting for the DJ to prepare the next set.")
            }
            waitAtEndForVoice()
            return
        }
        val files = resolveBatch(next)
        if (files.isEmpty()) {
            _ui.update {
                it.copy(error = "The next set's tracks are not downloaded on this device.")
            }
            return
        }
        advancing = true
        val previousBatch = localBatch
        // Install the local queue before notifying the server so the queue
        // callback recognizes this as our own change.
        localBatch = next
        nextBatch = null
        localFiles = files
        expectedPaths = files.map { it.absolutePath }
        boundaryStarted = false
        waitingForVoice = false
        waitingAtEnd = false
        waitJob?.cancel()
        waitJob = null
        _ui.update {
            it.copy(
                currentBatch = next,
                nextBatch = null,
                voice = if (voice.speaking.value) DjVoiceStatus.Speaking else DjVoiceStatus.Idle
            )
        }
        if (!voice.speaking.value) player.setDucked(false)
        if (player.repeatFlow.value) player.toggleRepeat()
        player.playFiles(files, 0)
        val shouldIntro = !boundaryIntroPlayed
        boundaryIntroPlayed = false
        if (shouldIntro && next.shouldComment && next.introClipIds.isNotEmpty()) {
            scope.launch {
                delay(250)
                if (localBatch?.id == next.id && _ui.value.active) playIntro(next)
            }
        }
        advancing = false
        if (previousBatch != null) {
            Log.i(TAG, "AI DJ batch ${previousBatch.number} -> ${next.number}")
        }
    }

    private fun startPolling() {
        if (pollJob?.isActive == true) return
        pollJob = scope.launch {
            while (isActive && _ui.value.active) {
                requestStateNow()
                val interval = when {
                    waitingForVoice -> 1_500L
                    nextBatch == null -> 2_000L
                    else -> 5_000L
                }
                delay(interval)
            }
        }
    }

    private fun requestState() {
        scope.launch { requestStateNow() }
    }

    private suspend fun requestStateNow() {
        try {
            applyServerState(AiDjApi.state())
        } catch (e: Exception) {
            Log.w(TAG, "state refresh failed: ${e.message}")
        }
    }

    private fun applyServerState(state: AiDjState) {
        if (state.status != "active" && _ui.value.active) {
            resetLocal(keepUiServer = true)
            onSessionChanged?.invoke(null)
            _ui.update {
                it.copy(
                    server = state,
                    active = false,
                    voice = DjVoiceStatus.Idle,
                    currentBatch = state.currentBatch,
                    nextBatch = state.nextBatch,
                    error = state.error
                )
            }
            return
        }
        if (_ui.value.active) {
            val local = localBatch
            val candidate = state.nextBatch
            if (candidate != null && candidate.id != local?.id &&
                candidate.id != nextBatch?.id
            ) {
                nextBatch = candidate
            }
            if (candidate == null && nextBatch?.id == local?.id) {
                nextBatch = null
            }
            _ui.update {
                it.copy(
                    loading = false,
                    server = state,
                    currentBatch = localBatch,
                    nextBatch = nextBatch,
                    error = state.error
                )
            }
        } else {
            _ui.update {
                it.copy(
                    loading = false,
                    server = state,
                    currentBatch = state.currentBatch,
                    nextBatch = state.nextBatch,
                    error = state.error
                )
            }
        }
    }

    private fun resetLocal(keepUiServer: Boolean) {
        sessionId = null
        localBatch = null
        nextBatch = null
        localFiles = emptyList()
        expectedPaths = emptyList()
        lastServerFile = null
        boundaryStarted = false
        boundaryIntroPlayed = false
        waitingForVoice = false
        waitingAtEnd = false
        advancing = false
        pollJob?.cancel()
        pollJob = null
        waitJob?.cancel()
        waitJob = null
        voice.stop()
        player.setDucked(false)
        if (!keepUiServer) {
            _ui.update { AiDjUiState() }
        }
    }

    private fun duckLevel(): Float =
        _ui.value.server?.settings?.duckLevel?.toFloat()?.coerceIn(0.02f, 0.8f) ?: 0.18f

    private fun duckLeadSeconds(): Int =
        max(3, (_ui.value.server?.settings?.voiceLeadSeconds ?: 10))

    companion object {
        private const val TAG = "AiDj"
    }
}
