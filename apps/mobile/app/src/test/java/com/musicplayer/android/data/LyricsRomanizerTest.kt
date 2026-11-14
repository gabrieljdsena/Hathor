package com.musicplayer.android.data

import org.junit.Assert.assertEquals
import org.junit.Test

class LyricsRomanizerTest {

    @Test
    fun `romanizes hiragana and katakana`() {
        assertEquals("konnichiha", LyricsRomanizer.romanize("こんにちは"))
        assertEquals("kyaku", LyricsRomanizer.romanize("きゃく"))
        assertEquals("shashin", LyricsRomanizer.romanize("しゃしん"))
        assertEquals("gakkou", LyricsRomanizer.romanize("がっこう"))
        assertEquals("koohii", LyricsRomanizer.romanize("コーヒー"))
    }

    @Test
    fun `preserves LRC timestamps and kanji`() {
        assertEquals(
            "[00:12.34] konnichiha\n[00:14.00] 東京",
            LyricsRomanizer.romanize("[00:12.34]こんにちは\n[00:14.00]東京", isLrc = true)
        )
    }

    @Test
    fun `leaves latin text untouched`() {
        assertEquals("Hello world", LyricsRomanizer.romanize("Hello world"))
    }
}
