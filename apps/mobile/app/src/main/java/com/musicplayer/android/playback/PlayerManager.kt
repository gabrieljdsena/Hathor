package com.musicplayer.android.playback

import android.content.Context
import android.media.MediaPlayer
import android.os.SystemClock
import android.util.Log
import com.musicplayer.android.data.PodcastChapter
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.cos
import kotlin.math.sin

/**
 * Local playback with gapless handoff + crossfade (desktop playback.py
 * crossfade/gapless engine port).
 *
 * Gapless handoff is always on, independent of crossfade: the active
 * player's end-of-stream callback advances the instant a track ends, and a
 * 200ms monitor backs it up. Both paths are guarded so stale/duplicate end
 * signals can neither skip nor stop tracks (an advance requires the elapsed
 * play time to have actually reached duration - 0.5s; an idle stream is
 * never stopped just to reload it).
 *
 * Crossfade (auto-advance only; manual next/prev stay instant): dual-output
 * model — the active player plus one dedicated fade player. Near track end
 * the incoming starts on the idle output while the outgoing fades; ownership
 * flips at completion. The monitor triggers it; ramps abort cleanly on any
 * manual action (generation tokens); pause mid-ramp settles the ramp first,
 * then pauses. Equal-power curve (cos/sin) at 50ms granularity.
 *
 * Desktop parity otherwise: queue over on-device mp3s, play/pause/next/prev,
 * seek + position ticker, shuffle (upcoming tail), repeat-one, saved-queue
 * restore, MediaSession/notification via PlayerService.
 */
class PlayerManager(appContext: Context, private val musicDir: () -> File) {

    private val appCtx = appContext.applicationContext
    private val scope = CoroutineScope(Dispatchers.Main)
    private var player: MediaPlayer? = null

    /** Dedicated crossfade output (desktop _xfade_chan equivalent). */
    private var fadePlayer: MediaPlayer? = null
    private var ticker: Job? = null

    private var queue: List<File> = emptyList()
    private var index: Int = -1
    // Unshuffled source order (desktop unshuffled_song_list): shuffle only
    // reorders the upcoming tail, and OFF restores around the current file.
    private var unshuffled: List<File> = emptyList()
    private var shuffled = false
    private var repeatOne = false

    // --- Crossfade / gapless state (desktop _out/_ramping/_xfade_gen model) ---
    var crossfadeEnabled: Boolean = false
        private set
    var crossfadeSeconds: Float = 5f
        private set
    private val rampLock = Any()
    private var ramping: Boolean = false
    private var rampGen: Int = 0
    private var rampStartMs: Long = 0L
    private var finishRampNow: Boolean = false
    // Dedupes double auto-advances (completion callback + monitor backup)
    // from the same track index.
    private var autoIndexClaim: Int = -1
    // Wall-clock play-time base for end guards (desktop last_play_time).
    private var trackStartWallMs: Long = 0L
    private var startOffsetMs: Int = 0

    /** Fired when the file ORDER changes (persist the queue). */
    var onQueueChanged: ((List<File>) -> Unit)? = null

    /** Fired when the current track changes (persist current_song). */
    var onTrackChanged: ((String) -> Unit)? = null

    private val _queueFlow = MutableStateFlow<List<File>>(emptyList())
    val queueFlow: StateFlow<List<File>> = _queueFlow.asStateFlow()

    private val _indexFlow = MutableStateFlow(-1)
    val indexFlow: StateFlow<Int> = _indexFlow.asStateFlow()

    private val _shuffleFlow = MutableStateFlow(false)
    val shuffleFlow: StateFlow<Boolean> = _shuffleFlow.asStateFlow()

    private val _repeatFlow = MutableStateFlow(false)
    val repeatFlow: StateFlow<Boolean> = _repeatFlow.asStateFlow()

    private val _current = MutableStateFlow<File?>(null)
    val current: StateFlow<File?> = _current.asStateFlow()

    private val _playing = MutableStateFlow(false)
    val playing: StateFlow<Boolean> = _playing.asStateFlow()

    private val _positionMs = MutableStateFlow(0)
    val positionMs: StateFlow<Int> = _positionMs.asStateFlow()

    private val _durationMs = MutableStateFlow(0)
    val durationMs: StateFlow<Int> = _durationMs.asStateFlow()

    /** Live-apply crossfade prefs (desktop set_crossfade); 0..12s clamped. */
    fun setCrossfade(enabled: Boolean, seconds: Float) {
        crossfadeEnabled = enabled
        crossfadeSeconds = seconds.coerceIn(0f, 12f)
        if (!enabled) cancelCrossfade()
    }

    // --- Podcast chapter-skip (desktop chapter_skip + web ChapterSkip) ---
    /** Master switch (desktop chapter_skip); live-applied, no restart needed. */
    var chapterSkipEnabled: Boolean = false
        private set

    /** Resolve chapter marks for a filename; wired to PodcastRepository. */
    var chapterProvider: (suspend (String) -> List<PodcastChapter>)? = null

    /** Classify a file as podcast; wired to the podcasts dir in MainActivity. */
    var isPodcastFile: ((File) -> Boolean)? = null

    /** Cached marks for the current file (name + start-sorted chapters). */
    private var chapterState: Pair<String, List<PodcastChapter>>? = null

    /** One-shot seek consumed on the next track start (chapter tap-to-play). */
    private var pendingStartSeek: Pair<String, Int>? = null

    /** Live-apply chapter auto-skip (desktop set_chapter_skip). */
    fun setChapterSkip(enabled: Boolean) {
        chapterSkipEnabled = enabled
        if (!enabled) {
            chapterState = null
            pendingStartSeek = null
        } else {
            queue.getOrNull(index)?.let { refreshChapters(it, seekFirst = false) }
        }
    }

    /** Play a single file, then land at positionMs (chapter time-chip). */
    fun playFileAtPosition(file: File, positionMs: Int) {
        pendingStartSeek = file.absolutePath to positionMs.coerceAtLeast(0)
        playFile(file)
    }

    private fun refreshChapters(file: File, seekFirst: Boolean) {
        val provider = chapterProvider ?: return
        if (!chapterSkipEnabled) return
        if (isPodcastFile?.invoke(file) == false) return
        val name = file.name
        scope.launch(Dispatchers.IO) {
            val chapters = try {
                provider(name).sortedBy { it.startSecs }
            } catch (_: Exception) {
                null
            }
            withContext(Dispatchers.Main) {
                if (queue.getOrNull(index)?.absolutePath != file.absolutePath) return@withContext
                chapterState = if (chapters.isNullOrEmpty()) null else name to chapters
                // Web parity: timestamped episodes start at the first chapter.
                if (seekFirst && chapters != null && chapters.isNotEmpty()) {
                    val first = chapters.first().startSecs
                    if (first > 1.0) seekTo((first * 1000).toInt())
                }
            }
        }
    }

    /** Gap-jump on the 200ms tick: past a chapter end with a next chapter. */
    private fun maybeChapterSkip(posMs: Int) {
        try {
            val (name, chapters) = chapterState ?: return
            if (chapters.isEmpty()) return
            if (queue.getOrNull(index)?.name != name) return
            val pos = posMs / 1000.0
            var current = -1
            for (i in chapters.indices) {
                if (pos >= chapters[i].startSecs) current = i else break
            }
            if (current < 0) return
            val end = chapters[current].endSecs ?: return // open-ended: never jump
            if (pos >= end && current + 1 < chapters.size) {
                val next = chapters[current + 1].startSecs
                if (next > pos) seekTo((next * 1000).toInt())
            }
        } catch (e: Exception) {
            Log.w(TAG, "chapter skip failed: ${e.message}")
        }
    }

    /** Rebuild the queue from the music dir (alphabetical, like send_song_list). */
    fun refreshQueue() {
        val files = musicDir().listFiles { f ->
            f.isFile && f.extension.equals("mp3", ignoreCase = true)
        }?.sortedBy { it.name } ?: emptyList()
        queue = files
        _queueFlow.value = files
        if (index >= files.size) {
            setIndex(-1)
        }
    }

    fun playFiles(files: List<File>, startIndex: Int = 0) {
        if (files.isEmpty()) return
        queue = files
        _queueFlow.value = files
        unshuffled = files
        shuffled = false
        _shuffleFlow.value = false
        onQueueChanged?.invoke(files)
        playAt(startIndex.coerceIn(files.indices))
    }

    /** Restore a persisted queue without autoplay (desktop opening behavior). */
    fun prepareFiles(files: List<File>, startIndex: Int = 0) {
        if (files.isEmpty()) return
        queue = files
        _queueFlow.value = files
        unshuffled = files
        prepareAt(startIndex.coerceIn(files.indices))
    }

    /** Desktop toggle_shuffle(): shuffle the upcoming tail only; OFF restores. */
    fun toggleShuffle() {
        val cur = queue.getOrNull(index) ?: return
        shuffled = !shuffled
        _shuffleFlow.value = shuffled
        if (shuffled) {
            unshuffled = queue
            queue = queue.take(index + 1) + queue.drop(index + 1).shuffled()
            _queueFlow.value = queue
        } else {
            val restored = if (unshuffled.isNotEmpty()) unshuffled else queue
            queue = restored
            _queueFlow.value = queue
            setIndex(restored.indexOfFirst { it.absolutePath == cur.absolutePath }.coerceAtLeast(0))
        }
        onQueueChanged?.invoke(queue)
    }

    /** Desktop repeat flag: repeat-one on auto-advance (play_next auto=True). */
    fun toggleRepeat() {
        repeatOne = !repeatOne
        _repeatFlow.value = repeatOne
    }

    /** Jump the queue to an index (queue sheet tap). */
    fun jumpTo(i: Int) {
        if (i in queue.indices) playAt(i)
    }

    /**
     * Queue editing, mapped from desktop's upcoming-only next_songs ops onto
     * this full-order queue (index-anchored so the current track never moves
     * under playback). Every mutation persists, like _persist_queue().
     */

    /** Desktop next_to_queue(): insert right after current (dedupe first). */
    fun playNext(file: File) {
        if (queue.isEmpty()) {
            playFiles(listOf(file), 0)
            return
        }
        val cur = queue.getOrNull(index)?.absolutePath ?: return
        val rest = queue.filterNot { it.absolutePath == file.absolutePath }
        val curIdx = rest.indexOfFirst { it.absolutePath == cur }.coerceAtLeast(0)
        val at = (curIdx + 1).coerceIn(0, rest.size)
        queue = rest.take(at) + file + rest.drop(at)
        _queueFlow.value = queue
        unshuffled = queue
        setIndex(curIdx)
        onQueueChanged?.invoke(queue)
    }

    /** Desktop add_to_queue(): append at the end (dedupe first). */
    fun addToQueue(file: File) {
        if (queue.isEmpty()) {
            playFiles(listOf(file), 0)
            return
        }
        val cur = currentFilePath()
        queue = queue.filterNot { it.absolutePath == file.absolutePath } + file
        _queueFlow.value = queue
        unshuffled = queue
        setIndex(queue.indexOfFirst { it.absolutePath == cur }.coerceAtLeast(0))
        onQueueChanged?.invoke(queue)
    }

    /** Desktop remove_from_queue(): remove any non-current row. */
    fun removeAt(i: Int) {
        if (i !in queue.indices || i == index) return
        val cur = currentFilePath()
        queue = queue.filterIndexed { idx, _ -> idx != i }
        _queueFlow.value = queue
        unshuffled = queue
        setIndex(queue.indexOfFirst { it.absolutePath == cur }.coerceAtLeast(0))
        onQueueChanged?.invoke(queue)
    }

    /** Desktop reorder_queue(): move a row; current stays anchored by file. */
    fun move(from: Int, to: Int) {
        if (from !in queue.indices || to !in queue.indices || from == to) return
        val cur = currentFilePath()
        val mutable = queue.toMutableList()
        val item = mutable.removeAt(from)
        mutable.add(to, item)
        queue = mutable
        _queueFlow.value = queue
        unshuffled = queue
        setIndex(queue.indexOfFirst { it.absolutePath == cur }.coerceAtLeast(0))
        onQueueChanged?.invoke(queue)
    }

    /** Desktop clear_queue(): drop everything upcoming, keep current. */
    fun clearUpcoming() {
        val cur = queue.getOrNull(index) ?: return
        queue = listOf(cur)
        _queueFlow.value = queue
        unshuffled = queue
        setIndex(0)
        onQueueChanged?.invoke(queue)
    }

    private fun currentFilePath(): String? = queue.getOrNull(index)?.absolutePath

    private fun setIndex(i: Int) {
        index = i
        _indexFlow.value = i
        autoIndexClaim = -1
    }

    fun playFile(file: File) {
        val i = queue.indexOfFirst { it.absolutePath == file.absolutePath }
        if (i >= 0) playAt(i)
        else playFiles(listOf(file), 0)
    }

    /** Pause if playing (preview handoff); no-op otherwise. */
    fun pause() {
        if (isRamping()) {
            // A pause during a ramp settles the ramp first so the settled
            // output is what actually gets paused (bounded wait, off-main).
            requestRampFinish()
            scope.launch(Dispatchers.IO) {
                val deadline = SystemClock.elapsedRealtime() + 1500
                while (isRamping() && SystemClock.elapsedRealtime() < deadline) delay(20)
                withContext(Dispatchers.Main) { doPause() }
            }
        } else {
            doPause()
        }
    }

    private fun doPause() {
        val p = player
        if (p != null && p.isPlaying) {
            try {
                p.pause()
            } catch (e: Exception) {
                Log.w(TAG, "pause failed: ${e.message}")
            }
            stampStart(safePosition(p))
            _playing.value = false
            stopTicker()
        }
    }

    fun toggle() {
        ensureService()
        val p = player
        if (p == null) {
            if (queue.isEmpty()) refreshQueue()
            if (queue.isEmpty()) return
            playAt(if (index in queue.indices) index else 0)
            return
        }
        if (p.isPlaying) {
            pause()
        } else {
            settleRampSync()
            try {
                p.start()
            } catch (e: Exception) {
                Log.w(TAG, "resume failed: ${e.message}")
                return
            }
            stampStart(safePosition(p))
            _playing.value = true
            startTicker()
        }
    }

    fun next() {
        advance(manual = true)
    }

    fun prev() {
        cancelCrossfade()
        if (queue.isEmpty()) return
        val p = player
        // Desktop-like: restart song if >3s in, else step back.
        if (p != null && safePosition(p) > 3000) {
            try {
                p.seekTo(0)
            } catch (e: Exception) {
                Log.w(TAG, "restart failed: ${e.message}")
            }
            stampStart(0)
            _positionMs.value = 0
        } else {
            playAt((index - 1 + queue.size) % queue.size)
        }
    }

    fun seekTo(ms: Int) {
        // Manual seeks always operate on a single well-defined output.
        cancelCrossfade()
        try {
            player?.seekTo(ms)
            stampStart(ms)
            _positionMs.value = ms
        } catch (e: Exception) {
            Log.w(TAG, "seek failed: ${e.message}")
        }
    }

    fun release() {
        stopTicker()
        cancelCrossfade()
        try {
            player?.stop()
            player?.release()
        } catch (_: Exception) {
        }
        player = null
        _playing.value = false
    }

    /**
     * Guarantees the foreground playback service (MediaSession + notification)
     * is up whenever playback is initiated from any entry point.
     */
    private fun ensureService() {
        try {
            appCtx.startForegroundService(
                android.content.Intent(appCtx, PlayerService::class.java)
            )
        } catch (e: Exception) {
            Log.w(TAG, "ensureService failed: ${e.message}")
        }
    }

    /**
     * Desktop play_next(): repeat-one replays on AUTO advance only; a manual
     * next at the end of the queue stops (no repeat-all on desktop).
     */
    private fun advance(manual: Boolean) {
        cancelCrossfade()
        if (queue.isEmpty()) return
        if (!manual && repeatOne) {
            try {
                player?.seekTo(0)
                stampStart(0)
                _positionMs.value = 0
                autoIndexClaim = -1
                if (player?.isPlaying == false) player?.start()
            } catch (e: Exception) {
                Log.w(TAG, "repeat failed: ${e.message}")
            }
            return
        }
        val ni = index + 1
        if (ni >= queue.size) {
            try {
                player?.pause()
                player?.seekTo(0)
            } catch (_: Exception) {
            }
            _playing.value = false
            _positionMs.value = 0
            stopTicker()
            return
        }
        playAt(ni)
    }

    /**
     * Single auto-advance entry point (completion callback + monitor backup):
     * dedupes double signals from the same track index.
     */
    private fun autoAdvance() {
        synchronized(rampLock) {
            if (autoIndexClaim == index) return
            autoIndexClaim = index
        }
        advance(manual = false)
    }

    private fun playAt(i: Int) {
        val file = queue.getOrNull(i) ?: return
        cancelCrossfade()
        setIndex(i)
        ensureService()
        scope.launch(Dispatchers.IO) {
            try {
                try {
                    player?.stop()
                    player?.release()
                } catch (_: Exception) {
                }
                val mp = MediaPlayer()
                mp.setDataSource(file.absolutePath)
                mp.prepare()
                mp.setOnCompletionListener { guardedOnCompletion(it) }
                withContext(Dispatchers.Main) {
                    // A manual action during prepare wins: drop this instance.
                    if (index != i) {
                        try {
                            mp.release()
                        } catch (_: Exception) {
                        }
                        return@withContext
                    }
                    player = mp
                    _current.value = file
                    _durationMs.value = try { mp.duration } catch (_: Exception) { 0 }
                    _positionMs.value = 0
                    mp.start()
                    stampStart(0)
                    // Chapter tap-to-play lands here (consumed once, file-gated).
                    val pending = pendingStartSeek?.takeIf { it.first == file.absolutePath }
                    pendingStartSeek = null
                    if (pending != null) {
                        try {
                            mp.seekTo(pending.second)
                            stampStart(pending.second)
                            _positionMs.value = pending.second
                        } catch (_: Exception) {
                        }
                    }
                    _playing.value = true
                    startTicker()
                    onTrackChanged?.invoke(file.name)
                    refreshChapters(file, seekFirst = pending == null)
                }
            } catch (e: Exception) {
                Log.e(TAG, "play failed: ${file.name}", e)
                withContext(Dispatchers.Main) { _playing.value = false }
            }
        }
    }

    /** Load a track without starting it (launch restore path). */
    private fun prepareAt(i: Int) {
        val file = queue.getOrNull(i) ?: return
        cancelCrossfade()
        setIndex(i)
        scope.launch(Dispatchers.IO) {
            try {
                try {
                    player?.stop()
                    player?.release()
                } catch (_: Exception) {
                }
                val mp = MediaPlayer()
                mp.setDataSource(file.absolutePath)
                mp.prepare()
                mp.setOnCompletionListener { guardedOnCompletion(it) }
                withContext(Dispatchers.Main) {
                    if (index != i) {
                        try {
                            mp.release()
                        } catch (_: Exception) {
                        }
                        return@withContext
                    }
                    player = mp
                    _current.value = file
                    _durationMs.value = try { mp.duration } catch (_: Exception) { 0 }
                    _positionMs.value = 0
                    stampStart(0)
                    _playing.value = false
                    refreshChapters(file, seekFirst = false)
                }
            } catch (e: Exception) {
                Log.e(TAG, "prepare failed: ${file.name}", e)
            }
        }
    }

    // ==========================
    // Crossfade / gapless engine
    // ==========================

    private fun isRamping(): Boolean = synchronized(rampLock) { ramping }

    private fun requestRampFinish() {
        synchronized(rampLock) { finishRampNow = true }
    }

    /** Whether a ramp is live AND within its time budget (orphans recover). */
    private fun rampAlive(): Boolean = synchronized(rampLock) {
        if (!ramping) return false
        val budget = crossfadeSeconds.coerceIn(1f, 12f) * 1000 + 5000
        SystemClock.elapsedRealtime() - rampStartMs < budget
    }

    private fun safePosition(p: MediaPlayer): Int {
        return try {
            p.currentPosition
        } catch (_: Exception) {
            _positionMs.value
        }
    }

    private fun stampStart(offsetMs: Int) {
        startOffsetMs = offsetMs.coerceAtLeast(0)
        trackStartWallMs = SystemClock.elapsedRealtime()
    }

    private fun wallElapsedMs(): Long =
        startOffsetMs + (SystemClock.elapsedRealtime() - trackStartWallMs)

    /**
     * End-of-stream watcher: a live ramp owns the track ending (finish it
     * instead of advancing twice); stale/duplicate signals are ignored
     * unless the elapsed play time really reached duration - 0.5s. An idle
     * stream is never stopped just to reload it.
     */
    private fun guardedOnCompletion(mp: MediaPlayer) {
        if (isRamping()) {
            requestRampFinish()
            return
        }
        if (!_playing.value) return
        val cur = player ?: return
        if (cur !== mp) return // stale player instance
        val dur = try { mp.duration } catch (_: Exception) { 0 }
        val pos = try { mp.currentPosition } catch (_: Exception) { -1 }
        val reached = if (dur > 0 && pos >= 0) {
            pos >= dur - 500
        } else {
            wallElapsedMs() >= (if (dur > 0) dur - 500 else 0) && wallElapsedMs() > 1000
        }
        if (!reached) return
        autoAdvance()
    }

    /** Next song eligible for a crossfade, or null (auto-advance only). */
    private fun crossfadeTarget(): File? {
        if (!crossfadeEnabled || crossfadeSeconds <= 0f) return null
        if (repeatOne) return null
        val nxt = queue.getOrNull(index + 1) ?: return null
        val cur = queue.getOrNull(index) ?: return null
        if (nxt.absolutePath == cur.absolutePath) return null
        if (!nxt.exists()) return null
        return nxt
    }

    private fun maybeStartCrossfade(pos: Int, dur: Int) {
        if (isRamping()) return
        if (!crossfadeEnabled || crossfadeSeconds <= 0f) return
        if (repeatOne) return
        val windowMs = (crossfadeSeconds * 1000).toInt()
        if (dur <= windowMs) return // too short: the gapless handoff covers it
        val remaining = dur - pos
        if (remaining <= 500 || remaining > windowMs) return
        val nxt = crossfadeTarget() ?: return
        startRamp(nxt)
    }

    private fun startRamp(next: File) {
        val gen: Int = synchronized(rampLock) {
            if (ramping || !_playing.value) return
            rampGen++
            ramping = true
            rampStartMs = SystemClock.elapsedRealtime()
            finishRampNow = false
            rampGen
        }
        ensureService()
        scope.launch(Dispatchers.IO) { runRamp(gen, next) }
    }

    private fun runRamp(gen: Int, next: File) {
        val seconds = crossfadeSeconds.coerceIn(0.5f, 12f)
        val incoming = try {
            MediaPlayer().apply {
                setDataSource(next.absolutePath)
                prepare()
            }
        } catch (e: Exception) {
            Log.w(TAG, "crossfade load failed: ${e.message}")
            abortRamp(gen)
            return
        }
        synchronized(rampLock) {
            if (gen != rampGen) {
                releaseQuiet(incoming)
                return // cancelled during prepare; the canceller owns cleanup
            }
            fadePlayer = incoming
        }
        try {
            incoming.setVolume(0f, 0f)
            incoming.start()
        } catch (e: Exception) {
            Log.w(TAG, "crossfade start failed: ${e.message}")
            abortRamp(gen)
            return
        }
        // 50ms granularity, capped at 240 steps (no zipper-stepping).
        val steps = maxOf(30, minOf(240, (seconds / 0.05f).toInt()))
        val stepMs = (seconds * 1000 / steps).toLong()
        val halfPi = Math.PI / 2.0
        for (i in 1..steps) {
            try {
                Thread.sleep(stepMs)
            } catch (_: InterruptedException) {
                return
            }
            var finish = false
            synchronized(rampLock) {
                if (gen != rampGen) return // cancelled; the canceller owns cleanup
                finish = finishRampNow
            }
            if (finish) break
            if (!_playing.value) break // pausing: snap to the new song, then pause it
            val t = i.toFloat() / steps
            // Equal-power crossfade (what mixers/DAWs use): cos^2 + sin^2
            // stays 1 so perceived loudness holds steady. A linear ramp
            // dips ~3 dB in the middle.
            val outGain = cos(t * halfPi).toFloat()
            val inGain = sin(t * halfPi).toFloat()
            try {
                player?.setVolume(outGain, outGain)
                synchronized(rampLock) { fadePlayer }?.setVolume(inGain, inGain)
            } catch (_: Exception) {
            }
        }
        synchronized(rampLock) {
            if (gen != rampGen) return
            finishRampNow = false
        }
        commitRamp(gen, next)
    }

    private fun commitRamp(gen: Int, next: File) {
        val incoming = synchronized(rampLock) { fadePlayer } ?: run {
            abortRamp(gen)
            return
        }
        scope.launch(Dispatchers.Main) {
            val old: MediaPlayer?
            synchronized(rampLock) {
                if (gen != rampGen) {
                    // Lost a race with a manual action: drop the incoming.
                    releaseQuiet(incoming)
                    fadePlayer = null
                    return@launch
                }
                old = player
                player = incoming
                fadePlayer = null
                ramping = false
            }
            // Ownership flipped: the fade output now carries the song.
            releaseQuiet(old)
            _current.value = next
            val ni = queue.indexOfFirst { it.absolutePath == next.absolutePath }
            setIndex(if (ni >= 0) ni else index + 1)
            _durationMs.value = try { incoming.duration } catch (_: Exception) { 0 }
            _positionMs.value = 0
            stampStart(0)
            _playing.value = true
            startTicker()
            onTrackChanged?.invoke(next.name)
        }
    }

    private fun abortRamp(gen: Int) {
        var incoming: MediaPlayer? = null
        synchronized(rampLock) {
            if (gen == rampGen) {
                ramping = false
                finishRampNow = false
            }
            incoming = fadePlayer
            fadePlayer = null
        }
        releaseQuiet(incoming)
        try {
            player?.setVolume(1f, 1f)
        } catch (_: Exception) {
        }
    }

    /**
     * Abort any ramp and route audio back through the main output.
     * Called before manual actions (explicit play, next/prev, seek) so they
     * always operate on a single well-defined output.
     */
    private fun cancelCrossfade() {
        var incoming: MediaPlayer? = null
        synchronized(rampLock) {
            rampGen++
            ramping = false
            finishRampNow = false
            incoming = fadePlayer
            fadePlayer = null
        }
        releaseQuiet(incoming)
        try {
            player?.setVolume(1f, 1f)
        } catch (_: Exception) {
        }
    }

    /** Settle a live ramp synchronously when already off the main thread. */
    private fun settleRampSync() {
        if (!isRamping()) return
        requestRampFinish()
        // Only block when off-main; the toggle-resume path runs on main and
        // must never freeze the UI (the next startRamp regenerates anyway).
        if (Thread.currentThread().name.startsWith("main", ignoreCase = true)) return
        val deadline = SystemClock.elapsedRealtime() + 1500
        while (isRamping() && SystemClock.elapsedRealtime() < deadline) {
            try {
                Thread.sleep(20)
            } catch (_: InterruptedException) {
                return
            }
        }
    }

    private fun releaseQuiet(mp: MediaPlayer?) {
        if (mp == null) return
        try {
            mp.stop()
        } catch (_: Exception) {
        }
        try {
            mp.release()
        } catch (_: Exception) {
        }
    }

    private fun startTicker() {
        stopTicker()
        ticker = scope.launch {
            while (isActive) {
                try {
                    val p = player
                    if (p != null && _playing.value) {
                        val pos = try { p.currentPosition } catch (_: Exception) { -1 }
                        if (pos >= 0) _positionMs.value = pos
                        val dur = try { p.duration } catch (_: Exception) { 0 }
                        if (dur > 0 && pos >= 0) {
                            if (!rampAlive() && isRamping()) {
                                // Orphaned ramp can never wedge the handoff.
                                cancelCrossfade()
                            } else if (!isRamping()) {
                                if (pos >= dur - 300) {
                                    // Monitor backup for a missed end callback.
                                    autoAdvance()
                                } else {
                                    maybeStartCrossfade(pos, dur)
                                    maybeChapterSkip(pos)
                                }
                            }
                            // While ramping, end-detection defers to the ramp.
                        }
                    }
                } catch (_: Exception) {
                }
                delay(200)
            }
        }
    }

    private fun stopTicker() {
        ticker?.cancel()
        ticker = null
    }

    companion object {
        private const val TAG = "PlayerManager"
    }
}
