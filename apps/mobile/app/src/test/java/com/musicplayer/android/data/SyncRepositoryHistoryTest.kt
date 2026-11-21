package com.musicplayer.android.data

import android.app.Application
import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.HistorySyncQueueEntity
import com.musicplayer.android.data.local.MusicHistoryEntity
import com.musicplayer.android.engine.DownloadEngine
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
class SyncRepositoryHistoryTest {

    private data class CapturedRequest(
        val method: String?,
        val path: String?,
        val authorization: String?,
        val body: String
    )

    private lateinit var context: Context
    private lateinit var db: AppDatabase
    private lateinit var repo: SyncRepository
    private lateinit var server: ServerSocket
    private lateinit var serverThread: Thread
    private val captured = LinkedBlockingQueue<CapturedRequest>()

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        SyncConfig.init(context)
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        repo = SyncRepository(context, DownloadEngine(context), db)

        server = ServerSocket(0, 0, InetAddress.getByName("127.0.0.1"))
        serverThread = thread(isDaemon = true, name = "sync-history-test-server") {
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
                                parts.getOrNull(0),
                                parts.getOrNull(1),
                                headers["authorization"],
                                String(chars, 0, read)
                            )
                        )
                        val payload = "{}".toByteArray(Charsets.UTF_8)
                        val output = socket.getOutputStream()
                        output.write(
                            (
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                                    "Content-Length: ${payload.size}\r\n" +
                                    "Connection: close\r\n\r\n"
                                ).toByteArray(Charsets.US_ASCII)
                        )
                        output.write(payload)
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
        db.close()
    }

    @Test
    fun `queued history flushes as a scoped import`() = runBlocking {
        val id = db.playlistDao().insertMusicHistory(
            MusicHistoryEntity(songFile = "a.mp3", datePlayed = "2026-11-15 12:00:00")
        )
        db.historySyncQueueDao().enqueue(HistorySyncQueueEntity(id))

        repo.pushPendingHistory()

        val request = captured.poll(3, TimeUnit.SECONDS)
        assertNotNull(request)
        assertEquals("POST", request!!.method)
        assertEquals("/api/v1/sync/import", request.path)
        assertEquals("Bearer hth_test", request.authorization)
        val history = JSONObject(request.body).getJSONArray("MusicHistory")
        assertEquals(1, history.length())
        assertEquals(id, history.getJSONObject(0).getLong("Id"))
        assertEquals("a.mp3", history.getJSONObject(0).getString("SongFile"))
        assertTrue(db.historySyncQueueDao().pendingIds().isEmpty())
    }
}
