package com.musicplayer.android.data.update

import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File
import java.net.InetAddress
import java.net.ServerSocket
import java.security.MessageDigest
import java.util.UUID
import kotlin.concurrent.thread

@RunWith(RobolectricTestRunner::class)
@Config(application = android.app.Application::class)
class GitHubUpdateApiTest {

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

    private class LocalServer(private val handler: (path: String) -> Response) : AutoCloseable {
        private val socket = ServerSocket(0, 0, InetAddress.getByName("127.0.0.1"))
        private val worker = thread(isDaemon = true, name = "github-update-test") {
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
                        while (true) {
                            val line = reader.readLine() ?: break
                            if (line.isEmpty()) break
                        }
                        val response = handler(path)
                        val status = when (response.code) {
                            200 -> "OK"
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

    private fun asset(
        name: String,
        bytes: ByteArray,
        url: String,
        digest: String? = "sha256:${sha(bytes)}",
    ): JSONObject = JSONObject()
        .put("name", name)
        .put("size", bytes.size)
        .put("browser_download_url", url)
        .apply { if (digest != null) put("digest", digest) }

    private fun release(
        tag: String,
        code: Int,
        name: String,
        assets: List<JSONObject>,
        draft: Boolean = false,
        prerelease: Boolean = false,
    ): JSONObject = JSONObject()
        .put("tag_name", tag)
        .put("draft", draft)
        .put("prerelease", prerelease)
        .put("published_at", "2026-11-23T12:00:00Z")
        .put("assets", JSONArray(assets))

    private fun feed(vararg releases: JSONObject): String = JSONArray(releases.toList()).toString()

    @Test
    fun `latest picks the highest android release and ignores desktop and drafts`() {
        val oldBytes = byteArrayOf(1, 2, 3)
        val newBytes = byteArrayOf(4, 5, 6)
        val local = LocalServer { path ->
            when {
                path.startsWith("/releases") -> Response(
                    200,
                    feed(
                        release(
                            "android-v0.4.24", 27, "0.4.24-hathor",
                            listOf(asset("hathor-android-27-0.4.24-hathor.apk", oldBytes, "https://example/old.apk")),
                        ),
                        release(
                            "android-v0.4.25", 28, "0.4.25-hathor",
                            listOf(asset("hathor-android-28-0.4.25-hathor.apk", newBytes, "https://example/new.apk")),
                        ),
                        release(
                            "android-v0.4.26", 29, "0.4.26-hathor",
                            listOf(asset("hathor-android-29-0.4.26-hathor.apk", newBytes, "https://example/draft.apk")),
                            draft = true,
                        ),
                        release(
                            "desktop-v1.0.2", 99, "1.0.2-hathor",
                            listOf(asset("hathor-desktop-99-1.0.2-hathor.zip", newBytes, "https://example/desktop.zip")),
                        ),
                    ).toByteArray(),
                )
                else -> Response(404, ByteArray(0))
            }
        }
        server = local

        val result = GitHubUpdateApi(local.baseUrl + "/releases").latest()

        assertTrue(result is LatestResult.Success)
        val info = (result as LatestResult.Success).info
        assertEquals(28, info.versionCode)
        assertEquals("0.4.25-hathor", info.versionName)
        assertEquals("hathor-android-28-0.4.25-hathor.apk", info.fileName)
        assertEquals(sha(newBytes), info.sha256)
    }

    @Test
    fun `latest handles empty feeds, 404 and errors`() {
        var mode = "empty"
        val local = LocalServer { path ->
            if (!path.startsWith("/releases")) {
                Response(404, ByteArray(0))
            } else if (mode == "empty") {
                Response(200, "[]".toByteArray())
            } else if (mode == "404") {
                Response(404, ByteArray(0))
            } else {
                Response(500, ByteArray(0))
            }
        }
        server = local
        val api = GitHubUpdateApi(local.baseUrl + "/releases")

        assertEquals(LatestResult.NotFound, api.latest())
        mode = "404"
        assertEquals(LatestResult.NotFound, api.latest())
        mode = "500"
        assertTrue(api.latest() is LatestResult.Failure)
    }

    @Test
    fun `stable mode ignores prereleases`() {
        val bytes = byteArrayOf(1)
        val local = LocalServer {
            Response(
                200,
                feed(
                    release(
                        "android-v0.5.0", 30, "0.5.0-hathor",
                        listOf(asset("hathor-android-30-0.5.0-hathor.apk", bytes, "https://example/prerelease.apk")),
                        prerelease = true,
                    ),
                ).toByteArray(),
            )
        }
        server = local

        assertEquals(
            LatestResult.NotFound,
            GitHubUpdateApi(local.baseUrl + "/releases", includePrereleases = false).latest(),
        )
    }

    @Test
    fun `download streams the exact bytes`() {
        val payload = ByteArray(256 * 1024) { (it % 251).toByte() }
        val local = LocalServer { path ->
            when {
                path.startsWith("/releases") -> Response(
                    200,
                    feed(
                        release(
                            "android-v0.4.25", 28, "0.4.25-hathor",
                            listOf(asset("hathor-android-28-0.4.25-hathor.apk", payload, "https://example/asset")),
                        ),
                    ).toByteArray(),
                )
                else -> Response(200, payload, "application/vnd.android.package-archive")
            }
        }
        server = local
        val api = GitHubUpdateApi(local.baseUrl + "/releases")
        val info = (api.latest() as LatestResult.Success).info
        // The feed points at example.invalid for parsing; download the local copy.
        val localInfo = info.copy(downloadPath = local.baseUrl + "/download/apk")
        val out = File(System.getProperty("java.io.tmpdir"), "hathor-update-${UUID.randomUUID()}.apk")
        var lastBytes = 0L
        try {
            api.download(localInfo, out) { bytes, _ -> lastBytes = bytes }
            assertArrayEquals(payload, out.readBytes())
            assertEquals(payload.size.toLong(), lastBytes)
        } finally {
            out.delete()
        }
    }

    @Test
    fun `sha256sums fallback provides a digest for older assets`() {
        val bytes = byteArrayOf(7, 8, 9)
        var base = ""
        val sums = "${sha(bytes)}  hathor-android-28-0.4.25-hathor.apk\n"
        val local = LocalServer { path ->
            when {
                path.startsWith("/releases") -> Response(
                    200,
                    feed(
                        release(
                            "android-v0.4.25", 28, "0.4.25-hathor",
                            listOf(
                                asset(
                                    "hathor-android-28-0.4.25-hathor.apk",
                                    bytes,
                                    "https://example/asset",
                                    digest = null,
                                ),
                                JSONObject()
                                    .put("name", "SHA256SUMS.txt")
                                    .put("size", sums.length)
                                    .put("browser_download_url", "$base/sums"),
                            ),
                        ),
                    ).toByteArray(),
                )
                path == "/sums" -> Response(200, sums.toByteArray(), "text/plain")
                else -> Response(404, ByteArray(0))
            }
        }
        server = local
        base = local.baseUrl

        val result = GitHubUpdateApi(local.baseUrl + "/releases").latest()

        assertTrue(result is LatestResult.Success)
        assertEquals(sha(bytes), (result as LatestResult.Success).info.sha256)
        assertEquals(
            sha(bytes),
            GitHubUpdateApi.parseSums(sums, "hathor-android-28-0.4.25-hathor.apk"),
        )
    }
}
