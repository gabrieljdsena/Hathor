package com.musicplayer.android.playback

import com.musicplayer.android.data.api.PlaybackEvent
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

@OptIn(ExperimentalCoroutinesApi::class)
class PlaybackTelemetryTest {

    private fun telemetry(
        sent: MutableList<PlaybackEvent>,
        scope: kotlinx.coroutines.CoroutineScope,
        sender: suspend (PlaybackEvent) -> Unit = { sent += it }
    ) = PlaybackTelemetry(
        sender = sender,
        clock = { 1_700_000_000_000L },
        newSegmentId = { "seg-1" },
        scope = scope
    )

    @Test
    fun `completed track uses the last position`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("a.mp3"), 0, 200_000, isPodcast = false)
        telemetry.onPlaybackTick(File("a.mp3"), 199_500, 200_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(1, sent.size)
        assertEquals("seg-1", sent[0].segmentId)
        assertEquals("a.mp3", sent[0].file)
        assertEquals(199.5, sent[0].positionSec, 0.001)
        assertEquals(200.0, sent[0].durationSec, 0.001)
        assertTrue(sent[0].completed)
        assertFalse(sent[0].skipped)
        assertEquals("2023-11-14T22:13:20Z", sent[0].startedAtUtc)
    }

    @Test
    fun `early stop is reported as skipped`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("a.mp3"), 0, 200_000, isPodcast = false)
        telemetry.onPlaybackTick(File("a.mp3"), 40_000, 200_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(1, sent.size)
        assertFalse(sent[0].completed)
        assertTrue(sent[0].skipped)
    }

    @Test
    fun `short tracks are neither completed nor skipped`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("short.mp3"), 0, 3_000, isPodcast = false)
        telemetry.onPlaybackTick(File("short.mp3"), 2_900, 3_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(1, sent.size)
        assertFalse(sent[0].completed)
        assertFalse(sent[0].skipped)
    }

    @Test
    fun `podcasts are never reported`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("episode.mp3"), 0, 600_000, isPodcast = true)
        telemetry.onPlaybackTick(File("episode.mp3"), 300_000, 600_000, isPodcast = true)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertTrue(sent.isEmpty())
    }

    @Test
    fun `a podcast closes the active music segment`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("song.mp3"), 0, 200_000, isPodcast = false)
        telemetry.onPlaybackTick(File("song.mp3"), 199_000, 200_000, isPodcast = false)
        telemetry.onPlaybackTick(File("episode.mp3"), 0, 600_000, isPodcast = true)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(1, sent.size)
        assertEquals("song.mp3", sent[0].file)
        assertTrue(sent[0].completed)
    }

    @Test
    fun `a track change closes the previous segment and starts the next`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("a.mp3"), 0, 200_000, isPodcast = false)
        telemetry.onPlaybackTick(File("a.mp3"), 20_000, 200_000, isPodcast = false)
        telemetry.onPlaybackTick(File("b.mp3"), 0, 180_000, isPodcast = false)
        telemetry.onPlaybackTick(File("b.mp3"), 179_000, 180_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(2, sent.size)
        assertEquals("a.mp3", sent[0].file)
        assertTrue(sent[0].skipped)
        assertEquals("b.mp3", sent[1].file)
        assertTrue(sent[1].completed)
    }

    @Test
    fun `a failed send never escapes telemetry`() = runTest {
        val telemetry = telemetry(
            sent = mutableListOf(),
            scope = this,
            sender = { throw IllegalStateException("offline") }
        )

        telemetry.onPlaybackTick(File("a.mp3"), 0, 200_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()
    }

    @Test
    fun `pause and resume keeps one segment`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("a.mp3"), 10_000, 200_000, isPodcast = false)
        // A pause does not call onPlaybackStopped; resume ticks the same file.
        telemetry.onPlaybackTick(File("a.mp3"), 20_000, 200_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(1, sent.size)
        assertEquals(20.0, sent[0].positionSec, 0.001)
    }

    @Test
    fun `repeat closes the finished segment and starts a fresh one`() = runTest {
        val sent = mutableListOf<PlaybackEvent>()
        val telemetry = telemetry(sent, this)

        telemetry.onPlaybackTick(File("a.mp3"), 199_000, 200_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        telemetry.onPlaybackTick(File("a.mp3"), 30_000, 200_000, isPodcast = false)
        telemetry.onPlaybackStopped()
        advanceUntilIdle()

        assertEquals(2, sent.size)
        assertTrue(sent[0].completed)
        assertFalse(sent[1].completed)
        assertTrue(sent[1].skipped)
    }
}
