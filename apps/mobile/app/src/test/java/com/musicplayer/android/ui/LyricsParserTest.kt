package com.musicplayer.android.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * LRC parser (desktop parseLRC parity): [mm:ss.xx] tags, two- or three-digit
 * centiseconds, empty lines rendered as the ♪ placeholder.
 */
class LyricsParserTest {

    @Test
    fun `parses centisecond and millisecond tags`() {
        val lines = parseLrc(
            """
            [00:12.34]First line
            [01:02.50]Second line
            [02:03.456]Third line
            """.trimIndent(),
        )

        assertEquals(3, lines.size)
        assertEquals(12.34, lines[0].timeSec, 0.0001)
        assertEquals(62.5, lines[1].timeSec, 0.0001)
        assertEquals(123.456, lines[2].timeSec, 0.0001)
        assertEquals("First line", lines[0].text)
    }

    @Test
    fun `keeps the first tag when a line repeats its timestamp`() {
        val lines = parseLrc("[00:05.00][00:09.00]Chorus")
        assertEquals(1, lines.size)
        assertEquals(5.0, lines[0].timeSec, 0.0001)
        assertEquals("Chorus", lines[0].text)
    }

    @Test
    fun `empty lyric text becomes the placeholder`() {
        val lines = parseLrc("[00:10.00]")
        assertEquals(1, lines.size)
        assertEquals("♪", lines[0].text)
    }

    @Test
    fun `skips untagged and malformed lines`() {
        val lines = parseLrc(
            """
            intro without timestamp
            [0:12.00]bad minute width
            [00:11.1]single fraction digit
            [00:20.00]kept
            """.trimIndent(),
        )
        assertEquals(1, lines.size)
        assertEquals(20.0, lines[0].timeSec, 0.0001)
    }

    @Test
    fun `empty input yields no lines`() {
        assertTrue(parseLrc("").isEmpty())
    }
}
