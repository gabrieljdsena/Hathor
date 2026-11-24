package com.musicplayer.android.data.update

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.api.AuthSession
import com.musicplayer.android.data.api.MemoryRefreshTokenStore
import kotlinx.coroutines.runBlocking
import java.io.File
import java.net.InetAddress
import java.net.ServerSocket
import java.security.MessageDigest
import java.util.UUID
import kotlin.concurrent.thread

@RunWith(RobolectricTestRunner::class)
@Config(application = android.app.Application::class)
class UpdateHttpApiTest {

    private var server: LocalServer? = null

    @After
    fun tearDown() {
        server?.close()
    }

    private data class Response(
        val code: Int,
        val body: ByteArray,
        val contentType: String = "application/json",
    )

    private class LocalServer(
        private val handler: (path: String, authorization: String?) -> Response,
    ) : AutoCloseable {
        private val socket = ServerSocket(0, 0, InetAddress.getByName("127.0.0.1"))
        private val worker = thread(isDaemon = true, name = "update-http-test") {
            while (!socket.isClosed) {
                val client = try {
                    socket.accept()
                } catch (_: Exception) {
                    break
                }
                client.use { c ->
                    try {
                        c.soTimeout = 5_000
                        val reader = c.getInputStream().bufferedReader(Charsets.UTF_8)
                        val request = reader.readLine() ?: return@use
                        val path = request.split(' ').getOrNull(1) ?: "/"
                        var authorization: String? = null
                        var contentLength = 0
                        while (true) {
                            val line = reader.readLine() ?: break
                            if (line.isEmpty()) break
                            val colon = line.indexOf(':')
                            if (colon > 0) {
                                val name = line.substring(0, colon).trim()
                                val value = line.substring(colon + 1).trim()
                                if (name.equals("Authorization", ignoreCase = true)) {
                                    authorization = value
                                } else if (name.equals("Content-Length", ignoreCase = true)) {
                                    contentLength = value.toIntOrNull() ?: 0
                                }
                            }
                        }
                        if (contentLength > 0) {
                            val body = CharArray(contentLength)
                            var read = 0
                            while (read < contentLength) {
                                val count = reader.read(body, read, contentLength - read)
                                if (count < 0) break
                                read += count
                            }
                        }
                        val response = handler(path, authorization)
                        val status = when (response.code) {
                            200 -> "OK"
                            401 -> "Unauthorized"
                            404 -> "Not Found"
                            else -> "Error"
                        }
                        val header = buildString {
                            append("HTTP/1.1 ${response.code} $status\r\n")
                            append("Content-Type: ${response.contentType}\r\n")
                            append("Content-Length: ${response.body.size}\r\n")
                            append("Connection: close\r\n\r\n")
                        }
                        c.getOutputStream().apply {
                            write(header.toByteArray(Charsets.UTF_8))
                            write(response.body)
                            flush()
                        }
                    } catch (_: Exception) {
                    }
                }
            }
        }

        val baseUrl = "http://127.0.0.1:${socket.localPort}"

        override fun close() {
            try {
                socket.close()
            } catch (_: Exception) {
            }
            worker.interrupt()
        }
    }

    private fun sha(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") {
            (it.toInt() and 0xff).toString(16).padStart(2, '0')
        }

    private fun metadata(bytes: ByteArray, code: Int = 6): String =
        """
        {
          "versionCode": $code,
          "versionName": "0.4.3-hathor",
          "fileName": "hathor-$code-0.4.3-hathor.apk",
          "sizeBytes": ${bytes.size},
          "sha256": "${sha(bytes)}",
          "publishedAtUtc": "2026-11-21T01:00:00+00:00",
          "downloadPath": "/api/v1/app/download/hathor-$code-0.4.3-hathor.apk"
        }
        """.trimIndent()

    @Test
    fun `latest parses 200 and maps 404 and 401`() {
        val payload = byteArrayOf(1, 2, 3, 4)
        var mode = "ok"
        val local = LocalServer { path, _ ->
            when {
                mode == "404" -> Response(404, """{"message":"none"}""".toByteArray())
                mode == "401" -> Response(401, ByteArray(0))
                path.endsWith("/latest") -> Response(200, metadata(payload).toByteArray())
                else -> Response(200, payload, "application/vnd.android.package-archive")
            }
        }
        server = local
        val api = HttpUpdateApi(local.baseUrl, "test-token")

        val ok = api.latest()
        assertTrue(ok is LatestResult.Success)
        assertEquals(6, (ok as LatestResult.Success).info.versionCode)

        mode = "404"
        assertEquals(LatestResult.NotFound, api.latest())

        mode = "401"
        assertTrue(api.latest() is LatestResult.Failure)
    }

    @Test
    fun `download streams the exact bytes`() {
        val payload = ByteArray(256 * 1024) { (it % 251).toByte() }
        val local = LocalServer { path, _ ->
            if (path.endsWith("/latest")) {
                Response(200, metadata(payload).toByteArray())
            } else {
                Response(200, payload, "application/vnd.android.package-archive")
            }
        }
        server = local
        val api = HttpUpdateApi(local.baseUrl, "test-token")
        val info = (api.latest() as LatestResult.Success).info
        val out = File(System.getProperty("java.io.tmpdir"), "hathor-update-${UUID.randomUUID()}.apk")
        var lastBytes = 0L
        try {
            api.download(info, out) { bytes, _ -> lastBytes = bytes }
            assertArrayEquals(payload, out.readBytes())
            assertEquals(payload.size.toLong(), lastBytes)
        } finally {
            out.delete()
        }
    }

    private fun tokens(access: String, refresh: String): String =
        """{"accessToken":"$access","refreshToken":"$refresh"}"""

    @Test
    fun `latest refreshes once and retries after 401`() = runBlocking {
        val payload = byteArrayOf(1, 2, 3, 4)
        var latestCalls = 0
        var refreshCalls = 0
        val local = LocalServer { path, auth ->
            when (path) {
                "/api/v1/auth/login" ->
                    Response(200, tokens("access-1", "refresh-1").toByteArray())
                "/api/v1/auth/refresh" -> {
                    refreshCalls++
                    Response(200, tokens("access-2", "refresh-2").toByteArray())
                }
                "/api/v1/app/latest" -> {
                    latestCalls++
                    if (auth == "Bearer access-2") {
                        Response(200, metadata(payload).toByteArray())
                    } else {
                        Response(401, ByteArray(0))
                    }
                }
                else -> Response(404, ByteArray(0))
            }
        }
        server = local
        val session = AuthSession(
            ApplicationProvider.getApplicationContext(),
            MemoryRefreshTokenStore(),
        )
        assertTrue(session.login(local.baseUrl, "alice", "pw").ok)
        val api = HttpUpdateApi(local.baseUrl, token = null, auth = session)

        val result = api.latest()

        assertTrue(result is LatestResult.Success)
        assertEquals(2, latestCalls)
        assertEquals(1, refreshCalls)
        assertEquals("access-2", session.accessToken())
    }

    @Test
    fun `download refreshes once and retries after 401`() = runBlocking {
        val payload = ByteArray(128 * 1024) { (it % 251).toByte() }
        var downloadCalls = 0
        var refreshCalls = 0
        val local = LocalServer { path, auth ->
            when {
                path == "/api/v1/auth/login" ->
                    Response(200, tokens("access-1", "refresh-1").toByteArray())
                path == "/api/v1/auth/refresh" -> {
                    refreshCalls++
                    Response(200, tokens("access-2", "refresh-2").toByteArray())
                }
                path == "/api/v1/app/latest" -> Response(200, metadata(payload).toByteArray())
                path.startsWith("/api/v1/app/download/") -> {
                    downloadCalls++
                    if (auth == "Bearer access-2") {
                        Response(200, payload, "application/vnd.android.package-archive")
                    } else {
                        Response(401, ByteArray(0))
                    }
                }
                else -> Response(404, ByteArray(0))
            }
        }
        server = local
        val session = AuthSession(
            ApplicationProvider.getApplicationContext(),
            MemoryRefreshTokenStore(),
        )
        assertTrue(session.login(local.baseUrl, "alice", "pw").ok)
        val api = HttpUpdateApi(local.baseUrl, token = null, auth = session)
        val info = (api.latest() as LatestResult.Success).info
        val out = File(System.getProperty("java.io.tmpdir"), "hathor-update-${UUID.randomUUID()}.apk")
        try {
            api.download(info, out) { _, _ -> }

            assertArrayEquals(payload, out.readBytes())
            assertEquals(2, downloadCalls)
            assertEquals(1, refreshCalls)
            assertEquals("access-2", session.accessToken())
        } finally {
            out.delete()
        }
    }
}
