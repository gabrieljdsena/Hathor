package com.musicplayer.android.engine

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Linkless-row search query builder (desktop parity). Pure JVM test: pins the
 * "title + artist + audio" shape and the "Artist - Title" recovery path.
 */
class SearchQueryTest {

    @Test
    fun `uses title and artist when both are known`() {
        assertEquals(
            "ytsearch1:Blue in Green Miles Davis audio",
            DownloadEngine.searchQuery("Blue in Green", "Miles Davis"),
        )
    }

    @Test
    fun `recovers artist from an Artist - Title title`() {
        assertEquals(
            "ytsearch1:Blue in Green Miles Davis audio",
            DownloadEngine.searchQuery("Miles Davis - Blue in Green", null),
        )
    }

    @Test
    fun `recovers artist case-insensitively from an Artist - Title title`() {
        assertEquals(
            "ytsearch1:So What Miles Davis audio",
            DownloadEngine.searchQuery("Miles Davis - So What", "unknown"),
        )
    }

    @Test
    fun `drops a placeholder artist when the title has no dash`() {
        assertEquals(
            "ytsearch1:Blue in Green audio",
            DownloadEngine.searchQuery("Blue in Green", "Unknown"),
        )
    }

    @Test
    fun `blank artist and extra whitespace collapse to one space`() {
        assertEquals(
            "ytsearch1:Blue in Green audio",
            DownloadEngine.searchQuery("  Blue   in Green  ", "   "),
        )
    }
}
