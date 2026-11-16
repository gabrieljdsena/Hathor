package com.musicplayer.android.data.api

import android.app.Application
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.net.InetAddress
import java.net.ServerSocket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class PlaybackEventsApiTest {

    private data class CapturedRequest(
        val method: String?,
        val path: String?,
        val authorization: String?,
        val body: String
    )

    private lateinit var server: ServerSocket
    private lateinit var serverThread: Thread
    private val captured = LinkedBlockingQueue<CapturedRequest>()

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        SyncConfig.init(context)
        // Raw socket: com.sun.net.httpserver is not part of the Android SDK
        // stubs this unit-test source set compiles against.
        server = ServerSocket(0, 0, InetAddress.getByName("127.0.0.1"))
        serverThread = thread(
            isDaemon = true,
            name = "playback-events-test-server"
        ) {
            while (!server.isClosed) {
                val socket = try {
                    server.accept()
                } catch (_: Exception) {
                    break
                }
                socket.use {
                    try {
                        socket.soTimeout = 5_000
                        val reader = socket.getInputStream().bufferedReader(Charsets.UTF_8)
                        val requestLine = reader.readLine() ?: return@use
                        val parts = requestLine.split(' ')
                        val headers = mutableMapOf<String, String>()
                        while (true) {
                            val line = reader.readLine() ?: break
                            if (line.isEmpty()) break
                            val colon = line.indexOf(':')
                            if (colon > 0) {
                                headers[line.substring(0, colon).trim().lowercase()] =
                                    line.substring(colon + 1).trim()
                            }
                        }
                        val length = headers["content-length"]?.toIntOrNull() ?: 0
                        val chars = CharArray(length)
                        var read = 0
                        while (read < length) {
                            val count = reader.read(chars, read, length - read)
                            if (count < 0) break
                            read += count
                        }
                        captured.put(
                            CapturedRequest(
                                method = parts.getOrNull(0),
                                path = parts.getOrNull(1),
                                authorization = headers["authorization"],
                                body = String(chars, 0, read)
                            )
                        )
                        val output = socket.getOutputStream()
                        output.write(
                            (
                                "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n" +
                                    "Connection: close\r\n\r\n"
                                ).toByteArray(Charsets.US_ASCII)
                        )
                        output.flush()
                    } catch (_: Exception) {
                    }
                }
            }
        }
        SyncConfig.save("http://127.0.0.1:${server.localPort}", "hth_test")
    }

    @After
    fun tearDown() {
        server.close()
        serverThread.join(2_000)
        SyncConfig.clearRuntime()
    }

    @Test
    fun `posts one playback segment with the scoped key`() = runBlocking {
        PlaybackEventsApi.record(
            PlaybackEvent(
                segmentId = "segment-1",
                file = "a.mp3",
                positionSec = 199.5,
                durationSec = 200.0,
                completed = true,
                skipped = false,
                startedAtUtc = "2026-11-15T12:00:00Z"
            )
        )

        val request = captured.poll(3, TimeUnit.SECONDS)
        assertNotNull(request)
        assertEquals("POST", request!!.method)
        assertEquals("/api/v1/player/playback-events", request.path)
        assertEquals("Bearer hth_test", request.authorization)
        val json = JSONObject(request.body)
        assertEquals("segment-1", json.getString("SegmentId"))
        assertEquals("a.mp3", json.getString("File"))
        assertTrue(json.getBoolean("Completed"))
    }
}
