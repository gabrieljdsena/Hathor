package com.musicplayer.android.data.api

import org.json.JSONObject

/**
 * One playback segment (a single music-track play) for Discover quality
 * signals. Podcasts are never reported, and the server dedupes by SegmentId
 * so retrying the same segment is safe.
 */
data class PlaybackEvent(
    val segmentId: String,
    val file: String,
    val positionSec: Double,
    val durationSec: Double,
    val completed: Boolean,
    val skipped: Boolean,
    val startedAtUtc: String
) {
    fun toJson(): JSONObject = JSONObject()
        .put("SegmentId", segmentId)
        .put("File", file)
        .put("PositionSec", positionSec)
        .put("DurationSec", durationSec)
        .put("Completed", completed)
        .put("Skipped", skipped)
        .put("StartedAtUtc", startedAtUtc)
}

/**
 * Playback telemetry is best-effort: a missing/offline Web API must never
 * affect local playback. The server owns the recommendation weighting.
 */
object PlaybackEventsApi {

    suspend fun record(event: PlaybackEvent) {
        if (!SyncConfig.isConfigured()) return
        HathorApi().postNoContent("/player/playback-events", event.toJson())
    }
}
