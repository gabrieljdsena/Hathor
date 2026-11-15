package com.musicplayer.android.playback

/**
 * Whether the last track of a DJ batch has entered the voice lead window.
 * Pure so the boundary contract is testable without a MediaPlayer.
 */
internal fun djBoundaryDue(positionMs: Int, durationMs: Int, leadSeconds: Int): Boolean {
    if (durationMs <= 0 || positionMs < 0) return false
    val lead = leadSeconds.coerceIn(1, 30) * 1000
    return positionMs >= durationMs - lead
}
