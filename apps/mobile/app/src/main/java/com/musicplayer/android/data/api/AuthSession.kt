package com.musicplayer.android.data.api

import android.content.Context
import android.os.Build
import android.util.Base64
import android.util.Log
import com.musicplayer.android.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL

data class AuthView(
    val url: String,
    val mode: String,
    val username: String,
    val signedIn: Boolean,
    val configured: Boolean,
    val keySet: Boolean,
    val keyHint: String,
    val message: String,
)

data class LoginResult(val ok: Boolean, val message: String)

/**
 * Single auth boundary for the mobile Web API.
 *
 * Access JWTs stay in memory. The rotating refresh token is encrypted with an
 * Android Keystore key and persisted by [SecureTokenStore]. Passwords are only
 * sent once to /auth/login and are never stored.
 */
class AuthSession(
    context: Context,
    private val store: RefreshTokenStore = SecureTokenStore(context),
) {
    private val appContext = context.applicationContext
    private val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val lock = Any()

    private var baseUrl = ""
    private var mode = MODE_NONE
    private var username = ""
    private var apiKey = ""
    private var refreshToken = ""
    private var accessToken = ""
    private var accessExpiresAt = 0L
    private var message = ""

    init {
        reload()
    }

    fun reload() {
        synchronized(lock) {
            val storedUrl = prefs.getString(KEY_URL, null)
                ?: BuildConfig.API_BASE_URL.orEmpty()
            val storedKey = prefs.getString(KEY_TOKEN, null)
                ?: BuildConfig.API_TOKEN.orEmpty()
            val storedUser = prefs.getString(KEY_USERNAME, null).orEmpty()
            var storedMode = prefs.getString(KEY_MODE, null).orEmpty()
            if (storedMode.isBlank()) {
                storedMode = when {
                    storedKey.isNotBlank() -> MODE_KEY
                    storedUser.isNotBlank() -> MODE_LOGIN
                    else -> MODE_NONE
                }
            }
            baseUrl = normalizeUrl(storedUrl)
            username = storedUser
            mode = storedMode
            apiKey = if (mode == MODE_KEY) storedKey else ""
            refreshToken = if (mode == MODE_LOGIN) store.load() else ""
            accessToken = ""
            accessExpiresAt = 0L
            message = when {
                mode == MODE_LOGIN && refreshToken.isNotBlank() -> "Signed in as $username."
                mode == MODE_LOGIN -> "Sign in to connect."
                mode == MODE_KEY && apiKey.isNotBlank() -> "API key configured."
                else -> "No Web API configured."
            }
        }
    }

    fun baseUrl(): String = synchronized(lock) { baseUrl }

    fun configured(): Boolean = synchronized(lock) {
        baseUrl.isNotBlank() && (
            (mode == MODE_KEY && apiKey.isNotBlank()) ||
                (mode == MODE_LOGIN && username.isNotBlank() && refreshToken.isNotBlank())
            )
    }

    fun signedIn(): Boolean = synchronized(lock) {
        mode == MODE_LOGIN && username.isNotBlank() && refreshToken.isNotBlank()
    }

    fun view(): AuthView = synchronized(lock) {
        AuthView(
            url = baseUrl,
            mode = mode,
            username = username,
            signedIn = mode == MODE_LOGIN && refreshToken.isNotBlank(),
            configured = baseUrl.isNotBlank() && (
                (mode == MODE_KEY && apiKey.isNotBlank()) ||
                    (mode == MODE_LOGIN && username.isNotBlank() && refreshToken.isNotBlank())
                ),
            keySet = apiKey.isNotBlank(),
            keyHint = maskKey(apiKey),
            message = message,
        )
    }

    /**
     * Current access token. Blocks only when a refresh is needed; call from a
     * background/IO thread (all API clients already do).
     *
     * @param failedToken token that just received a 401; if another request
     * already rotated it, reuse the new token instead of refreshing again.
     */
    fun accessToken(failedToken: String? = null, force: Boolean = false): String {
        synchronized(lock) {
            if (mode == MODE_KEY) return apiKey
            if (mode != MODE_LOGIN || refreshToken.isBlank()) return ""
            if (failedToken != null && accessToken.isNotBlank() && accessToken != failedToken) {
                return accessToken
            }
            if (!force && accessToken.isNotBlank() &&
                accessExpiresAt > System.currentTimeMillis() + ACCESS_SKEW_MS
            ) {
                return accessToken
            }
            return refreshLocked()
        }
    }

    private fun refreshLocked(): String {
        if (baseUrl.isBlank() || refreshToken.isBlank()) return ""
        val response = request(
            "$baseUrl/api/v1/auth/refresh",
            "POST",
            JSONObject().put("refreshToken", refreshToken),
            null,
        )
        if (response.code !in 200..299) {
            if (response.code == 400 || response.code == 401 || response.code == 403) {
                clearSessionState("Your session expired. Sign in again.")
            } else {
                message = "Could not refresh the Web API session. Check the connection."
            }
            return ""
        }
        return try {
            applyTokens(response.body)
        } catch (e: Exception) {
            Log.w(TAG, "Invalid refresh response", e)
            message = "The Web API returned an invalid session."
            ""
        }
    }

    suspend fun login(
        url: String,
        user: String,
        password: String,
        rememberMe: Boolean = true,
    ): LoginResult = withContext(Dispatchers.IO) {
        val normalized = normalizeUrl(url)
        if (normalized.isBlank()) return@withContext LoginResult(false, "Server URL is required.")
        if (user.isBlank()) return@withContext LoginResult(false, "Username is required.")
        if (password.isBlank()) return@withContext LoginResult(false, "Password is required.")

        val response = request(
            "$normalized/api/v1/auth/login",
            "POST",
            JSONObject()
                .put("username", user.trim())
                .put("password", password)
                .put("rememberMe", rememberMe)
                .put("deviceLabel", deviceLabel()),
            null,
        )
        if (response.code == 401 || response.code == 403) {
            return@withContext LoginResult(false, "Invalid username or password.")
        }
        if (response.code !in 200..299) {
            return@withContext LoginResult(
                false,
                apiMessage(response, "Sign-in failed (${response.code})"),
            )
        }
        try {
            synchronized(lock) {
                baseUrl = normalized
                username = user.trim()
                mode = MODE_LOGIN
                apiKey = ""
                applyTokens(response.body)
            }
            LoginResult(true, message)
        } catch (e: Exception) {
            Log.w(TAG, "Invalid login response", e)
            LoginResult(false, "The Web API returned an invalid session.")
        }
    }

    suspend fun logout(): LoginResult = withContext(Dispatchers.IO) {
        val (url, access, refresh) = synchronized(lock) {
            Triple(baseUrl, accessToken, refreshToken)
        }
        if (url.isNotBlank() && (access.isNotBlank() || refresh.isNotBlank())) {
            request(
                "$url/api/v1/auth/logout",
                "POST",
                JSONObject().put("refreshToken", refresh.ifBlank { JSONObject.NULL }),
                access.ifBlank { null },
            )
        }
        synchronized(lock) {
            accessToken = ""
            refreshToken = ""
            accessExpiresAt = 0L
            apiKey = ""
            mode = if (username.isNotBlank()) MODE_LOGIN else MODE_NONE
            message = "Signed out."
            prefs.edit()
                .putString(KEY_URL, baseUrl)
                .putString(KEY_MODE, mode)
                .putString(KEY_USERNAME, username)
                .remove(KEY_TOKEN)
                .apply()
            store.clear()
        }
        LoginResult(true, "Signed out.")
    }

    fun saveApiKey(url: String, token: String): LoginResult {
        val normalized = normalizeUrl(url)
        if (normalized.isBlank()) return LoginResult(false, "Server URL is required.")
        if (token.isBlank()) return LoginResult(false, "API key is required.")
        if (!token.startsWith("hth_")) return LoginResult(false, "The API key must start with hth_.")
        synchronized(lock) {
            baseUrl = normalized
            mode = MODE_KEY
            username = ""
            apiKey = token.trim()
            refreshToken = ""
            accessToken = ""
            accessExpiresAt = 0L
            message = "API key configured."
            prefs.edit()
                .putString(KEY_URL, baseUrl)
                .putString(KEY_TOKEN, apiKey)
                .putString(KEY_MODE, mode)
                .remove(KEY_USERNAME)
                .apply()
            store.clear()
        }
        return LoginResult(true, "API key saved.")
    }

    fun clear() {
        synchronized(lock) {
            baseUrl = ""
            mode = MODE_NONE
            username = ""
            apiKey = ""
            refreshToken = ""
            accessToken = ""
            accessExpiresAt = 0L
            message = ""
            prefs.edit()
                .remove(KEY_URL)
                .remove(KEY_TOKEN)
                .remove(KEY_MODE)
                .remove(KEY_USERNAME)
                .apply()
            store.clear()
        }
    }

    suspend fun testConnection(): String = withContext(Dispatchers.IO) {
        val token = accessToken()
        if (baseUrl.isBlank() || token.isBlank()) return@withContext "Sign in first."
        val health = request("$baseUrl/api/health/live", "GET", null, token)
        if (health.code !in 200..299) {
            return@withContext "Connection failed: ${apiMessage(health, "HTTP ${health.code}")}"
        }
        val me = request("$baseUrl/api/v1/auth/me", "GET", null, token)
        val count = request("$baseUrl/api/v1/songs/count", "GET", null, token)
        if (me.code !in 200..299) {
            return@withContext "Connected, but authentication failed (${me.code})."
        }
        val name = try {
            JSONObject(me.body).optString("username")
        } catch (_: Exception) {
            ""
        }
        val songs = count.body.trim()
        "Connected${if (name.isBlank()) "" else " as $name"} - $songs song(s) visible."
    }

    private fun applyTokens(body: String): String {
        val obj = JSONObject(body)
        val access = obj.optString("accessToken").ifBlank { obj.optString("AccessToken") }
        val refresh = obj.optString("refreshToken").ifBlank { obj.optString("RefreshToken") }
        if (access.isBlank() || refresh.isBlank()) throw IllegalStateException("missing tokens")
        accessToken = access
        refreshToken = refresh
        accessExpiresAt = jwtExpiry(access)
        mode = MODE_LOGIN
        message = "Signed in as $username."
        prefs.edit()
            .putString(KEY_URL, baseUrl)
            .putString(KEY_MODE, mode)
            .putString(KEY_USERNAME, username)
            .remove(KEY_TOKEN)
            .apply()
        if (!store.save(refresh)) {
            message = "Signed in as $username (secure storage unavailable)."
        }
        return access
    }

    private fun clearSessionState(reason: String) {
        accessToken = ""
        refreshToken = ""
        accessExpiresAt = 0L
        mode = if (username.isNotBlank()) MODE_LOGIN else MODE_NONE
        message = reason
        store.clear()
        prefs.edit()
            .putString(KEY_URL, baseUrl)
            .putString(KEY_MODE, mode)
            .putString(KEY_USERNAME, username)
            .remove(KEY_TOKEN)
            .apply()
    }

    private data class HttpResult(val code: Int, val body: String)

    private fun request(
        url: String,
        method: String,
        body: JSONObject?,
        token: String?,
    ): HttpResult {
        var conn: HttpURLConnection? = null
        return try {
            conn = URL(url).openConnection() as HttpURLConnection
            conn.requestMethod = method
            conn.connectTimeout = 20_000
            conn.readTimeout = 30_000
            conn.setRequestProperty("User-Agent", "hathor-android/1")
            conn.setRequestProperty("Accept", "application/json")
            if (!token.isNullOrBlank()) {
                conn.setRequestProperty("Authorization", "Bearer $token")
            }
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty()
            HttpResult(code, text)
        } catch (e: Exception) {
            HttpResult(0, e.message.orEmpty())
        } finally {
            conn?.disconnect()
        }
    }

    private fun apiMessage(response: HttpResult, fallback: String): String {
        if (response.body.isBlank()) return fallback
        return try {
            JSONObject(response.body).optString("message").ifBlank { fallback }
        } catch (_: Exception) {
            fallback
        }
    }

    private fun jwtExpiry(token: String): Long {
        return try {
            val payload = token.split('.')[1]
            val bytes = Base64.decode(payload, Base64.URL_SAFE or Base64.NO_WRAP)
            val exp = JSONObject(String(bytes, Charsets.UTF_8)).optLong("exp", 0L)
            if (exp > 0) exp * 1000L else System.currentTimeMillis() + 12 * 60_000L
        } catch (_: Exception) {
            System.currentTimeMillis() + 12 * 60_000L
        }
    }

    companion object {
        const val NOT_CONFIGURED =
            "No Web API configured. Set the server URL in Settings and sign in."

        private const val TAG = "AuthSession"
        private const val PREFS = "hathor_api"
        private const val KEY_URL = "base_url"
        private const val KEY_TOKEN = "token"
        private const val KEY_MODE = "auth_mode"
        private const val KEY_USERNAME = "auth_username"
        private const val MODE_NONE = "none"
        private const val MODE_KEY = "key"
        private const val MODE_LOGIN = "login"
        private const val ACCESS_SKEW_MS = 60_000L

        fun normalizeUrl(value: String): String {
            var raw = value.trim()
            if (raw.isBlank()) return ""
            if (!raw.contains("://")) raw = "http://$raw"
            if (!raw.startsWith("http://") && !raw.startsWith("https://")) return ""
            raw = raw.trimEnd('/')
            for (suffix in listOf("/api/v1", "/api")) {
                if (raw.endsWith(suffix)) raw = raw.removeSuffix(suffix)
            }
            return try {
                if (URI(raw).host.isNullOrBlank()) "" else raw
            } catch (_: Exception) {
                ""
            }
        }

        fun maskKey(key: String): String {
            if (key.isBlank()) return ""
            if (key.length <= 12) {
                return key.take(4) + "*".repeat(maxOf(4, key.length - 4))
            }
            return key.take(8) + "..." + key.takeLast(4)
        }

        fun deviceLabel(): String {
            val model = Build.MODEL.orEmpty().trim()
            return if (model.isBlank()) "Android" else "Android ($model)"
        }
    }
}
