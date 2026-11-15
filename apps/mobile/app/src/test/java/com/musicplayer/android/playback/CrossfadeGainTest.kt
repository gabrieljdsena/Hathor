package com.musicplayer.android.playback

import org.junit.Assert.assertEquals
import org.junit.Test

class CrossfadeGainTest {

    @Test
    fun `user volume scales the equal-power endpoints`() {
        assertEquals(0f, crossfadeGain(0.4f, 0f), 0.0001f)
        assertEquals(0.4f, crossfadeGain(0.4f, 1f), 0.0001f)
        assertEquals(1f, crossfadeGain(1f, 1f), 0.0001f)
    }

    @Test
    fun `midpoint follows the equal-power curve`() {
        val mid = kotlin.math.sin((Math.PI / 4.0)).toFloat()
        assertEquals(0.5f * mid, crossfadeGain(0.5f, mid), 0.0001f)
    }

    @Test
    fun `out-of-range inputs are clamped`() {
        assertEquals(0f, crossfadeGain(0f, 1f), 0.0001f)
        assertEquals(1f, crossfadeGain(2f, 2f), 0.0001f)
        assertEquals(0f, crossfadeGain(0.8f, -1f), 0.0001f)
    }
}
