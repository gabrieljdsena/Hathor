package com.musicplayer.android.data

import android.app.Application
import org.json.JSONArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class LyricsSearchFilterTest {

    @Test
    fun `cleans bracket qualifiers like the web`() {
        assertEquals(
            "Song" to "Artist",
            cleanLyricsQuery("Song (Remastered 2011)", "Artist")
        )
        assertEquals(
            "Song" to "Artist",
            cleanLyricsQuery("Song [Live at Home]", "Artist")
        )
    }

    @Test
    fun `splits Artist - Title only when the artist is unknown`() {
        assertEquals(
            "Have You Ever Seen The Rain" to "Creedence Clearwater Revival",
            cleanLyricsQuery(
                "Creedence Clearwater Revival - Have You Ever Seen The Rain",
                "Unknown"
            )
        )
        assertEquals(
            "Artist - Title" to "Real Artist",
            cleanLyricsQuery("Artist - Title", "Real Artist")
        )
    }

    @Test
    fun `chapter names split title first like the web`() {
        assertEquals(
            "Beggin" to "Frankie Valli",
            splitLyricsChapter("Beggin - Frankie Valli")
        )
        assertEquals("Intro" to "", splitLyricsChapter("Intro"))
        assertEquals(
            "Sway" to "Dean Martin",
            splitLyricsChapter("Sway (Remastered) - Dean Martin")
        )
    }

    @Test
    fun `mirrors web lrclib filters`() {
        val hits = parseLyricsSearchHits(
            JSONArray(
                """
                [
                  {"id":1,"trackName":"A","artistName":"B","syncedLyrics":"[00:01.00] x","plainLyrics":null},
                  {"id":2,"trackName":"Instrumental","artistName":"B","syncedLyrics":"[00:01.00] x","instrumental":true},
                  {"id":3,"trackName":"Empty","artistName":"B","syncedLyrics":null,"plainLyrics":null},
                  {"id":1,"trackName":"A","artistName":"B","syncedLyrics":"[00:01.00] x","plainLyrics":null}
                ]
                """.trimIndent()
            )
        )
        assertEquals(1, hits.size)
        assertEquals("A", hits[0].trackName)
        assertEquals("[00:01.00] x", hits[0].syncedLyrics)
        assertNull(hits[0].plainLyrics)
    }

    @Test
    fun `caps suggestions at ten`() {
        val arr = JSONArray()
        for (i in 0 until 14) {
            arr.put(
                org.json.JSONObject()
                    .put("id", i)
                    .put("trackName", "Track $i")
                    .put("artistName", "Artist")
                    .put("syncedLyrics", "[00:01.00] x")
            )
        }
        assertEquals(10, parseLyricsSearchHits(arr).size)
    }
}
