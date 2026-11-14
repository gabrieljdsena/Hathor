package com.musicplayer.android.data.api

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Sync contract decoding (packages/contracts camelCase JSON). Robolectric is
 * used because org.json is an Android framework class.
 */
@RunWith(RobolectricTestRunner::class)
@Config(application = android.app.Application::class)
class SyncApiTest {

    @Test
    fun `parses library rows from a flat delta payload`() {
        val delta = JSONObject(
            """
            {
              "songs": [
                {"file":"a.mp3","downloadedLink":"https://x/a","title":"A","dateDownloadUtc":"2024-01-02T03:04:05Z","artist":"Artist A"},
                {"file":"b.mp3","title":""}
              ],
              "podcasts": [
                {"file":"ep.mp3","title":"Episode","dateDownloadUtc":"2024-02-03T00:00:00Z","artist":"Show"}
              ],
              "playlists": [{"id":7,"title":"Road trip","description":"songs","thumbnail":null}],
              "songLinks": [{"id":1,"songFile":"a.mp3","playlistId":7,"dateAddedUtc":"2024-01-03T00:00:00Z"}],
              "lyrics": [
                {"id":2,"songFile":"a.mp3","lyricsJson":"[{\"text\":\"hi\"}]","offsetMs":250},
                {"id":3,"songFile":"a.mp3::chapter:1","lyricsJson":"{}","offsetMs":0}
              ],
              "musicHistory": [{"id":4,"songFile":"a.mp3","datePlayedUtc":"2024-01-04T00:00:00Z"}],
              "playlistHistory": [{"id":5,"playlistId":7,"datePlayedUtc":"2024-01-05T00:00:00Z"}],
              "podcastTags": [{"id":6,"name":"tech"}],
              "podcastTagLinks": [{"id":8,"podcastFile":"ep.mp3","tagId":6}],
              "podcastChapters": [
                {"id":9,"podcastFile":"ep.mp3","name":"Intro","startSecs":0.0,"endSecs":12.5},
                {"id":10,"podcastFile":"ep.mp3","name":"Open end","startSecs":12.5,"endSecs":null}
              ],
              "dailyMix": [{"mixDate":"2024-01-06","songFilesJson":"[\"a.mp3\"]"}],
              "deletions": [{"tableName":"songs","rowKey":"gone.mp3"}]
            }
            """.trimIndent(),
        )

        val snap = SyncApi.parseSnapshot(delta)

        assertEquals(2, snap.songs.size)
        assertEquals("A", snap.songs[0].title)
        assertEquals("Artist A", snap.songs[0].artist)
        assertEquals("https://x/a", snap.songs[0].downloadedLink)
        // Empty title falls back to the file name.
        assertEquals("b.mp3", snap.songs[1].title)
        assertNull(snap.songs[1].downloadedLink)

        assertEquals(1, snap.podcasts.size)
        assertEquals("Episode", snap.podcasts[0].title)

        assertEquals(1, snap.playlists.size)
        assertEquals(7L, snap.playlists[0].id)
        assertNull(snap.playlists[0].thumbnail)

        assertEquals(1, snap.songLinks.size)
        assertEquals(7L, snap.songLinks[0].playlistId)

        // Chapter pseudo-lyrics rows must never become lyric entries.
        assertEquals(1, snap.lyrics.size)
        assertEquals(250, snap.lyrics[0].offsetMs)

        assertEquals(1, snap.musicHistory.size)
        assertEquals(1, snap.playlistHistory.size)
        assertEquals(1, snap.tags.size)
        assertEquals(1, snap.tagLinks.size)

        assertEquals(2, snap.chapters.size)
        assertEquals(12.5, snap.chapters[0].endSecs!!, 0.0001)
        assertNull(snap.chapters[1].endSecs)

        assertEquals("2024-01-06", snap.mixes[0].mixDate)
        assertEquals("songs", snap.deletions[0].tableName)
        assertEquals("gone.mp3", snap.deletions[0].rowKey)
    }

    @Test
    fun `unwraps a snapshot envelope`() {
        val delta = JSONObject(
            """{"cursor":"abc","snapshot":{"songs":[{"file":"x.mp3","title":"X"}]}}""",
        )
        val snap = SyncApi.parseSnapshot(delta)
        assertEquals(1, snap.songs.size)
        assertEquals("X", snap.songs[0].title)
    }

    @Test
    fun `unknown or malformed dates fall back to now instead of crashing`() {
        val before = System.currentTimeMillis()
        val snap = SyncApi.parseSnapshot(
            JSONObject("""{"songs":[{"file":"x.mp3","title":"X","dateDownloadUtc":"not-a-date"}]}"""),
        )
        assertTrue(snap.songs[0].dateDownloadMillis >= before)
    }

    @Test
    fun `missing files accepts every casing the server has used`() {
        assertEquals(
            listOf("a.mp3", "b.mp3"),
            SyncApi.missingFiles(JSONObject("""{"missingFiles":["a.mp3","b.mp3"]}""")),
        )
        assertEquals(
            listOf("c.mp3"),
            SyncApi.missingFiles(JSONObject("""{"missing_files":["c.mp3"]}""")),
        )
        assertEquals(
            listOf("d.mp3"),
            SyncApi.missingFiles(JSONObject("""{"MissingFiles":["d.mp3"]}""")),
        )
        assertTrue(SyncApi.missingFiles(JSONObject("""{"ok":true}""")).isEmpty())
    }

    @Test
    fun `isoNow writes UTC instants`() {
        assertEquals("1970-01-01T00:00:00Z", SyncApi.isoNow(0))
        assertEquals("2024-01-02T03:04:05Z", SyncApi.isoNow(1704164645000))
    }

    @Test
    fun `sync file paths encode spaces as percent-20`() {
        val encoded = SyncApi.encodePathSegment("Say My Name.mp3")
        assertEquals("Say%20My%20Name.mp3", encoded)
        assertTrue("+ should not appear in a path segment", !encoded.contains("+"))
    }
}
