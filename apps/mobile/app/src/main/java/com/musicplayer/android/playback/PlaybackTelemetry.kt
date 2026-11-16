package com.musicplayer.android.playback

import com.musicplayer.android.data.api.PlaybackEvent
import com.musicplayer.android.data.api.PlaybackEventsApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.io.File
import java.time.Instant
import java.util.UUID
import kotlin.math.max
import kotlin.math.min

/** Player hooks consumed by [PlaybackTelemetry]. */
interface PlaybackObserver {
    fun onPlaybackTick(file: File, positionMs: Int, durationMs: Int, isPodcast: Boolean)
    fun onPlaybackStopped()
}

/**
 * One playback segment per non-podcast music track, mirroring the web and
 * desktop telemetry rules:
 *
 * - completed: known duration > 5s and the last position is within 5s of it;
 * - skipped: not completed, known duration, and last position < 50%;
 * - a changed file (or a podcast starting) closes the previous segment.
 *
 * Playback is authoritative. Delivery is fire-and-forget so a missing Web API
 * can never affect local audio.
 */
class PlaybackTelemetry(
    private val sender: suspend (PlaybackEvent) -> Unit = { PlaybackEventsApi.record(it) },
    private val clock: () -> Long = System::currentTimeMillis,
    private val newSegmentId: () -> String = { UUID.randomUUID().toString() },
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
) : PlaybackObserver {

    private data class ActiveSegment(
        val id: String,
        val file: String,
        val startedAtMs: Long,
        var lastPositionMs: Int,
        var lastDurationMs: Int
    )

    private val lock = Any()
    private var active: ActiveSegment? = null

    override fun onPlaybackTick(
        file: File,
        positionMs: Int,
        durationMs: Int,
        isPodcast: Boolean
    ) {
        val closed = synchronized(lock) {
            var previous: PlaybackEvent? = null
            val current = active
            if (current != null && (current.file != file.name || isPodcast)) {
                previous = closeLocked()
            }
            if (!isPodcast) {
                val position = max(0, positionMs)
                val duration = max(0, durationMs)
                val segment = active
                if (segment == null) {
                    active = ActiveSegment(
                        id = newSegmentId(),
                        file = file.name,
                        startedAtMs = clock(),
                        lastPositionMs = position,
                        lastDurationMs = duration
                    )
                } else {
                    segment.lastPositionMs = position
                    if (duration > 0) segment.lastDurationMs = duration
                }
            }
            previous
        }
        closed?.let(::deliver)
    }

    override fun onPlaybackStopped() {
        val payload = synchronized(lock) { closeLocked() }
        payload?.let(::deliver)
    }

    private fun closeLocked(): PlaybackEvent? {
        val segment = active ?: return null
        active = null

        val durationMs = max(0, segment.lastDurationMs)
        val positionMs = if (durationMs > 0) {
            min(max(0, segment.lastPositionMs), durationMs)
        } else {
            max(0, segment.lastPositionMs)
        }
        val completed = durationMs > 5_000 && positionMs >= durationMs - 5_000
        val skipped = !completed && durationMs > 0 && positionMs < durationMs * 0.5

        return PlaybackEvent(
            segmentId = segment.id,
            file = segment.file,
            positionSec = positionMs / 1000.0,
            durationSec = durationMs / 1000.0,
            completed = completed,
            skipped = skipped,
            startedAtUtc = Instant.ofEpochMilli(segment.startedAtMs).toString()
        )
    }

    private fun deliver(event: PlaybackEvent) {
        scope.launch {
            try {
                sender(event)
            } catch (_: Exception) {
                // Telemetry is best-effort; local playback owns the truth.
            }
        }
    }
}
