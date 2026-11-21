package com.musicplayer.android.data.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = android.app.Application::class)
class UpdateApiTest {

    private val valid = """
        {
          "versionCode": 6,
          "versionName": "0.4.3-hathor",
          "fileName": "hathor-6-0.4.3-hathor.apk",
          "sizeBytes": 130000000,
          "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "publishedAtUtc": "2026-11-21T01:00:00+00:00",
          "downloadPath": "/api/v1/app/download/hathor-6-0.4.3-hathor.apk"
        }
    """.trimIndent()

    @Test
    fun `parses the release contract`() {
        val info = HttpUpdateApi.parseLatest(valid)!!
        assertEquals(6, info.versionCode)
        assertEquals("0.4.3-hathor", info.versionName)
        assertEquals("hathor-6-0.4.3-hathor.apk", info.fileName)
        assertEquals(130000000L, info.sizeBytes)
        assertEquals("/api/v1/app/download/hathor-6-0.4.3-hathor.apk", info.downloadPath)
    }

    @Test
    fun `normalizes uppercase sha and rejects malformed metadata`() {
        assertEquals(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            HttpUpdateApi.parseLatest(valid.replace("0123456789abcdef", "0123456789ABCDEF"))!!.sha256,
        )
        assertNull(HttpUpdateApi.parseLatest("not json"))
        assertNull(HttpUpdateApi.parseLatest(valid.replace("\"sizeBytes\": 130000000", "\"sizeBytes\": 0")))
        assertNull(HttpUpdateApi.parseLatest(valid.replace("\"sha256\": \"0123", "\"sha256\": \"zzzz")))
        assertNull(
            HttpUpdateApi.parseLatest(
                valid.replace(
                    "hathor-6-0.4.3-hathor.apk",
                    "../hathor-6-0.4.3-hathor.apk",
                ),
            ),
        )
    }

    @Test
    fun `no-downgrade comparison is strictly greater`() {
        val info = HttpUpdateApi.parseLatest(valid)!!
        assertTrue(info.versionCode > 5)
        assertEquals(false, info.versionCode > 6)
        assertEquals(false, info.versionCode > 7)
    }
}
