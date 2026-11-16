package com.musicplayer.android.data.api

import android.app.Application
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class ApiContractTest {

    @After
    fun tearDown() {
        SyncConfig.clearRuntime()
    }

    @Test
    fun `runtime config overrides defaults and supports clearing`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        SyncConfig.init(context)

        SyncConfig.save("http://192.168.1.10:5051/", "hth_test")
        assertEquals("http://192.168.1.10:5051", SyncConfig.baseUrl())
        assertEquals("hth_test", SyncConfig.token())
        assertTrue(SyncConfig.isConfigured())

        SyncConfig.save("", "")
        assertFalse(SyncConfig.isConfigured())
    }

    @Test
    fun `parses the Discover feed`() {
        val feed = DiscoverApi.parse(
            JSONObject(
                """
                {
                  "date": "2026-11-14",
                  "cached": true,
                  "items": [{
                    "title": "New Song", "artist": "New Artist", "album": "Album",
                    "year": "2024", "genre": "Pop", "artworkUrl": "https://art/x.jpg",
                    "source": "similar", "score": 0.87, "mbid": "mb-1",
                    "reason": "Because you like X", "seedArtist": "X"
                  }]
                }
                """.trimIndent()
            )
        )

        assertEquals("2026-11-14", feed.date)
        assertTrue(feed.cached)
        assertEquals(1, feed.items.size)
        assertEquals("New Song", feed.items[0].title)
        assertEquals("similar", feed.items[0].source)
        assertEquals("Because you like X", feed.items[0].reason)
        assertEquals(0.87, feed.items[0].score, 0.0001)
    }

    @Test
    fun `parses AI DJ state and batches`() {        val state = AiDjApi.parse(
            JSONObject(
                """
                {
                  "enabled": true,
                  "configured": true,
                  "sessionId": "session-1",
                  "status": "active",
                  "settings": {
                    "batchSize": 3, "commentaryMode": "auto", "language": "en-US",
                    "discoveryLevel": 0.4, "brief": "warm indie",
                    "preferredGenres": "", "avoidArtists": "",
                    "duckLevel": 0.2, "voiceLeadSeconds": 12
                  },
                  "currentBatch": {
                    "id": "batch-1", "number": 1, "status": "ready",
                    "tracks": [{
                      "file": "a.mp3", "title": "A", "artist": "Artist",
                      "album": "Album", "duration": 123
                    }],
                    "introText": "Welcome", "introClipIds": ["clip-1"],
                    "introDurationSec": 5.5,
                    "outroText": null, "outroClipIds": [],
                    "outroDurationSec": 0,
                    "shouldComment": true, "mood": "warm", "reason": "energy"
                  },
                  "nextBatch": null,
                  "error": null
                }
                """.trimIndent()
            )
        )

        assertTrue(state.enabled)
        assertEquals("session-1", state.sessionId)
        assertEquals(3, state.settings?.batchSize)
        assertEquals("a.mp3", state.currentBatch?.tracks?.first()?.file)
        assertEquals(listOf("clip-1"), state.currentBatch?.introClipIds)
        assertEquals(5.5, state.currentBatch?.introDurationSec ?: 0.0, 0.001)
        assertEquals(12, state.settings?.voiceLeadSeconds)
        assertEquals("warm indie", state.settings?.brief)
        assertTrue(state.currentBatch?.shouldComment == true)
        assertNull(state.error)
    }

    @Test
    fun `parses youtube search and preview candidates`() {
        val hits = YoutubeApi.parseSearch(
            org.json.JSONArray(
                """
                [
                  {"id":"abc123","title":"A","uploader":"U","durationSec":123.4,"thumbnail":"t"},
                  {"id":null,"title":"bad"}
                ]
                """.trimIndent()
            )
        )
        assertEquals(1, hits.size)
        assertEquals("abc123", hits[0].id)
        assertEquals("https://www.youtube.com/watch?v=abc123", hits[0].pageUrl)

        val preview = YoutubeApi.parsePreview(
            JSONObject("""{"id":"abc123","embeddable":true,"audioUrl":"https://example/audio"}""")
        )
        assertTrue(preview.embeddable)
        assertEquals("https://example/audio", preview.audioUrl)
    }

    @Test
    fun `treats JSON nulls as absent instead of the string null`() {
        val state = AiDjApi.parse(
            JSONObject(
                """
                {
                  "enabled": true, "configured": true, "sessionId": null,
                  "status": "stopped", "settings": null,
                  "currentBatch": null, "nextBatch": null, "error": null
                }
                """.trimIndent()
            )
        )
        assertNull(state.sessionId)
        assertNull(state.settings)
        assertNull(state.currentBatch)
        assertNull(state.nextBatch)
        assertNull(state.error)
    }

    @Test
    fun `parses a cross-device resume spot`() {
        val spot = PlaybackApi.parseSpot(
            """
            {
              "device": "Firefox on Windows",
              "file": "a.mp3",
              "positionSec": 42.5,
              "isPodcast": false,
              "updatedUtc": "2026-11-14T12:00:00Z",
              "queueFiles": ["a.mp3", "b.mp3"],
              "queueSource": {"type": "playlist", "id": "7"}
            }
            """.trimIndent()
        )

        requireNotNull(spot)
        assertEquals("Firefox on Windows", spot.device)
        assertEquals("a.mp3", spot.file)
        assertEquals(42.5, spot.positionSec, 0.0001)
        assertEquals(listOf("a.mp3", "b.mp3"), spot.queueFiles)
        assertEquals("playlist", spot.queueSourceType)
        assertEquals("7", spot.queueSourceId)
        assertNull(PlaybackApi.parseSpot("null"))
    }

    @Test
    fun `maps playback telemetry to the server contract`() {
        val event = PlaybackEvent(
            segmentId = "segment-1",
            file = "a.mp3",
            positionSec = 199.5,
            durationSec = 200.0,
            completed = true,
            skipped = false,
            startedAtUtc = "2026-11-15T12:00:00Z"
        )
        val json = event.toJson()

        assertEquals("segment-1", json.getString("SegmentId"))
        assertEquals("a.mp3", json.getString("File"))
        assertEquals(199.5, json.getDouble("PositionSec"), 0.0001)
        assertEquals(200.0, json.getDouble("DurationSec"), 0.0001)
        assertTrue(json.getBoolean("Completed"))
        assertFalse(json.getBoolean("Skipped"))
        assertEquals("2026-11-15T12:00:00Z", json.getString("StartedAtUtc"))
    }
}
