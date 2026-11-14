package com.musicplayer.android.data.api

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

class HathorApiException(message: String, val statusCode: Int? = null) : Exception(message)

/**
 * Small REST client for the self-hosted Hathor Web API.
 *
 * The mobile app never hosts any of this logic: library sync, Discover and
 * AI DJ all run on the Web API. Auth is a scoped `hth_` API key (no login).
 */
class HathorApi(
    private val baseUrl: String = SyncConfig.baseUrl(),
    private val token: String = SyncConfig.token()
) {
    private val root = "${baseUrl.trimEnd('/')}/api/v1"

    suspend fun getJson(path: String, query: Map<String, String> = emptyMap()): JSONObject =
        JSONObject(getText(path, query))

    suspend fun getArray(path: String, query: Map<String, String> = emptyMap()): JSONArray =
        JSONArray(getText(path, query))

    suspend fun getText(path: String, query: Map<String, String> = emptyMap()): String =
        request("GET", path, query = query)

    suspend fun postJson(path: String, body: JSONObject? = null): JSONObject? {
        val text = request("POST", path, body = body)
        return text.takeIf { it.isNotBlank() }?.let { JSONObject(it) }
    }

    suspend fun postArray(path: String, body: JSONObject? = null): JSONArray {
        return JSONArray(request("POST", path, body = body))
    }

    suspend fun postNoContent(path: String, body: JSONObject? = null) {
        request("POST", path, body = body)
    }

    suspend fun putJson(path: String, body: JSONObject? = null): JSONObject? {
        val text = request("PUT", path, body = body)
        return text.takeIf { it.isNotBlank() }?.let { JSONObject(it) }
    }

    fun clipUrl(clipId: String): String {
        val encodedToken = URLEncoder.encode(token, "UTF-8")
        return "${baseUrl.trimEnd('/')}/api/v1/ai-dj/clips/${URLEncoder.encode(clipId, "UTF-8")}/audio?token=$encodedToken"
    }

    private suspend fun request(
        method: String,
        path: String,
        query: Map<String, String> = emptyMap(),
        body: JSONObject? = null
    ): String = withContext(Dispatchers.IO) {
        require(baseUrl.isNotBlank()) { SyncConfig.NOT_CONFIGURED }
        require(token.isNotBlank()) { SyncConfig.NOT_CONFIGURED }
        val url = buildString {
            append(root).append(path)
            if (query.isNotEmpty()) {
                append('?')
                append(query.entries.joinToString("&") { (k, v) ->
                    "${URLEncoder.encode(k, "UTF-8")}=${URLEncoder.encode(v, "UTF-8")}"
                })
            }
        }
        val conn = URL(url).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.connectTimeout = 15_000
            conn.readTimeout = 25_000
            conn.setRequestProperty("Authorization", "Bearer $token")
            conn.setRequestProperty("Accept", "application/json")
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty()
            if (code !in 200..299) {
                val message = try {
                    JSONObject(text).optString("message").ifBlank { "HTTP $code" }
                } catch (_: Exception) {
                    "HTTP $code"
                }
                throw HathorApiException(message, code)
            }
            text
        } finally {
            conn.disconnect()
        }
    }

    companion object {
        /**
         * Settings "Test connection": verifies URL + key and reports the
         * library count. No login is involved.
         */
        suspend fun testConnection(baseUrl: String, token: String): String {
            if (baseUrl.isBlank() || token.isBlank()) {
                return "Enter both a server URL and an API key."
            }
            return try {
                val api = HathorApi(baseUrl, token)
                val count = api.getText("/songs/count").trim()
                "Connected — $count song(s) visible to this key."
            } catch (e: HathorApiException) {
                when (e.statusCode) {
                    401, 403 -> "Connected, but the key was rejected (${e.message})."
                    else -> "Connection failed: ${e.message}"
                }
            } catch (e: Exception) {
                "Connection failed: ${e.message}"
            }
        }
    }
}
