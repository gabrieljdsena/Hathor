package com.musicplayer.android.playback

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DjBoundaryTest {

    @Test
    fun `voice starts inside the lead window`() {
        val duration = 200_000
        assertTrue(djBoundaryDue(190_500, duration, leadSeconds = 10))
        assertTrue(djBoundaryDue(199_000, duration, leadSeconds = 5))
    }

    @Test
    fun `voice stays quiet before the lead window`() {
        assertFalse(djBoundaryDue(150_000, 200_000, leadSeconds = 10))
        assertFalse(djBoundaryDue(0, 200_000, leadSeconds = 30))
    }

    @Test
    fun `unknown durations never trigger a boundary`() {
        assertFalse(djBoundaryDue(1_000, 0, leadSeconds = 10))
        assertFalse(djBoundaryDue(-1, 200_000, leadSeconds = 10))
    }

    @Test
    fun `lead is clamped to a safe range`() {
        assertTrue(djBoundaryDue(199_000, 200_000, leadSeconds = 0))
        assertTrue(djBoundaryDue(170_000, 200_000, leadSeconds = 99))
    }
}
