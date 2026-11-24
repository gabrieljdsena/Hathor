package com.musicplayer.android.data.api

import android.app.Application
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File
import java.net.InetAddress
import java.net.ServerSocket
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import kotlin.concurrent.thread

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class AuthSessionTest {

    private data class Request(
        val method: String,
        val path: String,
        val authorization: String?,
        val body: String,
    )

    private data class Reply(val code: Int, val body: String = "")

    private lateinit var context: Context
    private lateinit var server: ServerSocket
    private lateinit var serverThread: Thread
    private val requests = LinkedBlockingQueue<Request>()

    @Volatile
    private var responder: (Request) -> Reply = { Reply(404) }

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        server = ServerSocket(0, 0, InetAddress.getByName("127.0.0.1"))
        serverThread = thread(isDaemon = true, name = "auth-session-test-server") {
            serve()
        }
    }

    @After
    fun tearDown() {
        server.close()
        serverThread.join(2_000)
    }

    @Test
    fun `login stores only the refresh token and keeps the password out of preferences`() = runBlocking {
        responder = { request ->
            if (request.path == "/api/v1/auth/login") {
                Reply(200, tokens("access-1", "refresh-1"))
            } else {
                Reply(404, """{"message":"not found"}""")
            }
        }
        val store = MemoryRefreshTokenStore()
        val session = AuthSession(context, store)

        val result = session.login(baseUrl(), "alice", "correct horse battery staple")

        assertTrue(result.message, result.ok)
        assertEquals("access-1", session.accessToken())
        assertEquals("refresh-1", store.load())
        assertTrue(session.signedIn())
        assertEquals("alice", session.view().username)
        val loginRequest = requests.toList().first()
        assertEquals("/api/v1/auth/login", loginRequest.path)
        assertTrue(loginRequest.body.contains("\"password\":\"correct horse battery staple\""))
        val prefs = context.getSharedPreferences("hathor_api", Context.MODE_PRIVATE).all
        assertFalse(prefs.values.any { it?.toString()?.contains("correct horse") == true })
    }

    @Test
    fun `forced access refresh rotates and persists the new refresh token`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(200, tokens("access-2", "refresh-2"))
                else -> Reply(404)
            }
        }
        val store = MemoryRefreshTokenStore()
        val session = AuthSession(context, store)
        assertTrue(session.login(baseUrl(), "alice", "pw").ok)

        assertEquals("access-2", session.accessToken(force = true))

        assertEquals("refresh-2", store.load())
        val refreshRequest = requests.toList().first { it.path == "/api/v1/auth/refresh" }
        assertEquals("refresh-1", JSONObject(refreshRequest.body).getString("refreshToken"))
        assertTrue((refreshRequest.authorization ?: "").isBlank())
    }

    @Test
    fun `rejected refresh clears the stored session`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(401, """{"message":"expired"}""")
                else -> Reply(404)
            }
        }
        val store = MemoryRefreshTokenStore()
        val session = AuthSession(context, store)
        assertTrue(session.login(baseUrl(), "alice", "pw").ok)

        assertEquals("", session.accessToken(force = true))

        assertEquals("", store.load())
        assertFalse(session.signedIn())
        assertTrue(session.view().message.contains("expired", ignoreCase = true))
    }

    @Test
    fun `a new session restores the username and refresh token`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(200, tokens("access-2", "refresh-2"))
                else -> Reply(404)
            }
        }
        val store = MemoryRefreshTokenStore()
        assertTrue(AuthSession(context, store).login(baseUrl(), "alice", "pw").ok)

        val restored = AuthSession(context, store)

        assertTrue(restored.signedIn())
        assertTrue(restored.configured())
        assertEquals("alice", restored.view().username)
        assertEquals("access-2", restored.accessToken())
        assertEquals("refresh-2", store.load())
    }

    @Test
    fun `HathorApi refreshes once on 401 and retries with the new token`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(200, tokens("access-2", "refresh-2"))
                "/api/v1/songs/count" ->
                    if (request.authorization == "Bearer access-2") {
                        Reply(200, "42")
                    } else {
                        Reply(401, """{"message":"expired"}""")
                    }
                else -> Reply(404)
            }
        }
        val store = MemoryRefreshTokenStore()
        val session = AuthSession(context, store)
        assertTrue(session.login(baseUrl(), "alice", "pw").ok)

        val api = HathorApi(baseUrl(), token = null, auth = session)

        assertEquals("42", api.getText("/songs/count"))
        val countAuth = requests.toList()
            .filter { it.path == "/api/v1/songs/count" }
            .map { it.authorization }
        assertEquals(listOf("Bearer access-1", "Bearer access-2"), countAuth)
    }

    @Test
    fun `api key fallback still authenticates without a login`() {
        val session = AuthSession(context, MemoryRefreshTokenStore())

        val saved = session.saveApiKey(baseUrl(), "hth_scoped_key")

        assertTrue(saved.message, saved.ok)
        assertTrue(session.configured())
        assertTrue(session.view().keySet)
        assertEquals("hth_scoped_key", session.accessToken())
        assertFalse(session.signedIn())
        assertFalse(session.saveApiKey(baseUrl(), "not-a-key").ok)
    }

    @Test
    fun `secure token store never writes the refresh token in plaintext`() {
        val store = SecureTokenStore(context)
        val token = "refresh-secret-1"

        val saved = store.save(token)

        val prefs = context.getSharedPreferences("hathor_auth", Context.MODE_PRIVATE).all
        assertFalse(prefs.values.any { it?.toString()?.contains(token) == true })
        if (saved) {
            assertEquals(token, store.load())
        } else {
            assertEquals("", store.load())
        }
    }

    @Test
    fun `SyncApi refreshes once for delta after a 401`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(200, tokens("access-2", "refresh-2"))
                "/api/v1/sync/delta" ->
                    if (request.authorization == "Bearer access-2") {
                        Reply(200, """{"songs":[]}""")
                    } else {
                        Reply(401, """{"message":"expired"}""")
                    }
                else -> Reply(404)
            }
        }
        val session = AuthSession(context, MemoryRefreshTokenStore())
        assertTrue(session.login(baseUrl(), "alice", "pw").ok)
        val api = SyncApi(baseUrl(), token = null, auth = session)

        val delta = api.getDelta("")

        assertEquals(0, delta.getJSONArray("songs").length())
        val auths = requests.toList()
            .filter { it.path == "/api/v1/sync/delta" }
            .map { it.authorization }
        assertEquals(listOf("Bearer access-1", "Bearer access-2"), auths)
        assertEquals(
            1,
            requests.toList().count { it.path == "/api/v1/auth/refresh" },
        )
    }

    @Test
    fun `SyncApi refreshes once for file downloads after a 401`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(200, tokens("access-2", "refresh-2"))
                "/api/v1/sync/files/a.mp3" ->
                    if (request.authorization == "Bearer access-2") {
                        Reply(200, "abc")
                    } else {
                        Reply(401, """{"message":"expired"}""")
                    }
                else -> Reply(404)
            }
        }
        val session = AuthSession(context, MemoryRefreshTokenStore())
        assertTrue(session.login(baseUrl(), "alice", "pw").ok)
        val api = SyncApi(baseUrl(), token = null, auth = session)
        val dest = File(System.getProperty("java.io.tmpdir"), "hathor-sync-${UUID.randomUUID()}.mp3")

        try {
            assertTrue(api.downloadFile("a.mp3", "music", dest))
            assertEquals("abc", dest.readText())
        } finally {
            dest.delete()
        }
        assertEquals(
            1,
            requests.toList().count { it.path == "/api/v1/auth/refresh" },
        )
    }

    @Test
    fun `server error during refresh keeps the session`() = runBlocking {
        responder = { request ->
            when (request.path) {
                "/api/v1/auth/login" -> Reply(200, tokens("access-1", "refresh-1"))
                "/api/v1/auth/refresh" -> Reply(500, """{"message":"server error"}""")
                else -> Reply(404)
            }
        }
        val store = MemoryRefreshTokenStore()
        val session = AuthSession(context, store)
        assertTrue(session.login(baseUrl(), "alice", "pw").ok)

        assertEquals("", session.accessToken(force = true))

        assertEquals("refresh-1", store.load())
        assertTrue(session.signedIn())
        assertTrue(session.view().message.contains("Could not refresh", ignoreCase = true))
    }

    @Test
    fun `normalizeUrl accepts host without scheme and strips api suffix`() {
        assertEquals("http://192.168.1.10:5051", AuthSession.normalizeUrl("192.168.1.10:5051/"))
        assertEquals("https://hathor.example", AuthSession.normalizeUrl("https://hathor.example/api/v1"))
        assertEquals("", AuthSession.normalizeUrl("not a url"))
    }

    private fun baseUrl(): String = "http://127.0.0.1:${server.localPort}"

    private fun tokens(access: String, refresh: String): String =
        """{"accessToken":"$access","refreshToken":"$refresh"}"""

    private fun serve() {
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
                    val request = Request(
                        method = parts.getOrNull(0).orEmpty(),
                        path = parts.getOrNull(1)?.substringBefore('?').orEmpty(),
                        authorization = headers["authorization"],
                        body = String(chars, 0, read),
                    )
                    requests.put(request)
                    val reply = responder(request)
                    val body = reply.body.toByteArray(Charsets.UTF_8)
                    val head = (
                        "HTTP/1.1 ${reply.code} ${reason(reply.code)}\r\n" +
                            "Content-Type: application/json; charset=utf-8\r\n" +
                            "Content-Length: ${body.size}\r\n" +
                            "Connection: close\r\n\r\n"
                        ).toByteArray(Charsets.US_ASCII)
                    socket.getOutputStream().apply {
                        write(head)
                        write(body)
                        flush()
                    }
                } catch (_: Exception) {
                }
            }
        }
    }

    private fun reason(code: Int): String = when (code) {
        200 -> "OK"
        401 -> "Unauthorized"
        404 -> "Not Found"
        else -> "Error"
    }
}
