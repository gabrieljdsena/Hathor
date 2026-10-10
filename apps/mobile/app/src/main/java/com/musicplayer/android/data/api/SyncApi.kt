package com.musicplayer.android.data.api

import android.util.Log
import com.musicplayer.android.BuildConfig
import java.io.File
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.net.UnknownHostException
import org.json.JSONArray
import org.json.JSONObject

// Sync transport to the Hathor C# Web API (replaces the retired remote
// MySQL/TiDB leg — RemoteDb/RemoteReader/RemoteWriter + Connector/J).
// Same contract as the desktop api_sync.py client: GET delta?cursor= for
// incremental pulls, POST import for pushes (missingFiles come back for
// byte upload), GET/PUT files/{file} for raw MP3 bytes. Auth is a
// per-device hth_ key (library:read + library:write) over LAN.
// Config lives ONLY in local.properties via BuildConfig
// (API_BASE_URL/API_TOKEN — never hardcoded, never committed). Empty
// values = sync disabled, exactly like desktop.
//
// Stdlib only (HttpURLConnection + org.json) — no new dependency.

// Snapshot row shapes (same fields the JDBC reader produced, so the Room
// merge code below needs no translation).
data class RemoteSongRow(
    val file: String,
    val downloadedLink: String?,
    val title: String,
    val dateDownloadMillis: Long,
    val artist: String?,
)

data class RemotePlaylistRow(
    val id: Long,
    val title: String,
    val description: String?,
    val thumbnail: String?,
)

data class RemoteSongLinkRow(
    val id: Long,
    val songFile: String,
    val playlistId: Long,
    val dateAddedMillis: Long,
)

data class RemoteLyricRow(
    val id: Long,
    val songFile: String,
    val lyrics: String?,
    val offsetMs: Int,
)

data class RemoteMusicHistoryRow(
    val id: Long,
    val songFile: String,
    val datePlayedMillis: Long,
)

data class RemotePlaylistHistoryRow(
    val id: Long,
    val playlistId: Long,
    val datePlayedMillis: Long,
)

data class RemoteTagRow(val id: Long, val name: String)

data class RemoteTagLinkRow(val id: Long, val podcastFile: String, val tagId: Long)

data class RemoteChapterRow(
    val id: Long,
    val podcastFile: String,
    val name: String,
    val startSecs: Double,
    val endSecs: Double?,
)

data class RemoteMixRow(val mixDate: String, val songFilesJson: String)

data class RemoteDeletionRow(val tableName: String, val rowKey: String)

data class RemoteSnapshot(
    val songs: List<RemoteSongRow>,
    val podcasts: List<RemoteSongRow>,
    val playlists: List<RemotePlaylistRow>,
    val songLinks: List<RemoteSongLinkRow>,
    val lyrics: List<RemoteLyricRow>,
    val musicHistory: List<RemoteMusicHistoryRow>,
    val playlistHistory: List<RemotePlaylistHistoryRow>,
    val tags: List<RemoteTagRow>,
    val tagLinks: List<RemoteTagLinkRow>,
    val chapters: List<RemoteChapterRow>,
    val mixes: List<RemoteMixRow>,
    val deletions: List<RemoteDeletionRow>,
)

/** User-facing sync failure (message is shown verbatim in the UI). */
class SyncApiException(message: String) : Exception(message)

object SyncConfig {
    fun baseUrl(): String =
        try { BuildConfig.API_BASE_URL } catch (_: Exception) { "" }.trim().trimEnd('/')

    fun token(): String =
        try { BuildConfig.API_TOKEN } catch (_: Exception) { "" }.trim()

    fun isConfigured(): Boolean = baseUrl().isNotBlank() && token().isNotBlank()

    fun describe(): String =
        if (baseUrl().isNotBlank()) "Server: ${baseUrl()}" else "Sync off."

    const val NOT_CONFIGURED =
        "No sync server configured. Set API_BASE_URL and API_TOKEN in local.properties."
}

class SyncApi(val baseUrl: String, private val token: String) {
    private val base = "$baseUrl/api/v1/sync"

    // The server does not speak the file endpoints yet (pre-delta builds).
    private val serverMissing =
        "Sync server does not speak the sync-file endpoints yet " +
            "(need GET/PUT /api/v1/sync/files/{file} + delta). Pull the latest server build."

    private fun open(
        method: String,
        path: String,
        query: Map<String, String> = emptyMap(),
        contentType: String? = null,
    ): HttpURLConnection {
        val url = buildString {
            append(base).append(path)
            if (query.isNotEmpty()) {
                append("?")
                append(query.entries.joinToString("&") {
                    "${URLEncoder.encode(it.key, "UTF-8")}=${URLEncoder.encode(it.value, "UTF-8")}"
                })
            }
        }
        return (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            setRequestProperty("Authorization", "Bearer $token")
            setRequestProperty("User-Agent", "hathor-android/1")
            setRequestProperty("Accept", "application/json")
            if (contentType != null) setRequestProperty("Content-Type", contentType)
            connectTimeout = 30_000
            readTimeout = 120_000
        }
    }

    private fun readJson(conn: HttpURLConnection): JSONObject {
        val code = try {
            conn.responseCode
        } catch (e: UnknownHostException) {
            throw SyncApiException("Cannot reach sync server at $baseUrl. Is it on your LAN?")
        } catch (e: ConnectException) {
            throw SyncApiException("Cannot reach sync server at $baseUrl. Is it running?")
        }
        if (code == 401) {
            throw SyncApiException(
                "Sync server rejected the API key (401). Check API_TOKEN — " +
                    "it needs library:read + library:write scope.",
            )
        }
        if (code == 404) throw SyncApiException(serverMissing)
        if (code !in 200..299) {
            val detail = try {
                JSONObject(conn.errorStream?.bufferedReader()?.readText() ?: "").optString("message")
            } catch (_: Exception) { "" }
            throw SyncApiException("Sync server error ($code): ${detail.ifBlank { conn.responseMessage }}")
        }
        val text = conn.inputStream.bufferedReader().readText()
        try {
            return JSONObject(text)
        } catch (_: Exception) {
            throw SyncApiException("Sync server returned a non-JSON response.")
        }
    }

    fun getDelta(cursor: String): JSONObject {
        val conn = open("GET", "/delta", if (cursor.isBlank()) emptyMap() else mapOf("cursor" to cursor))
        try {
            return readJson(conn)
        } finally {
            conn.disconnect()
        }
    }

    fun postImport(payload: JSONObject): JSONObject {
        val body = payload.toString().toByteArray(Charsets.UTF_8)
        val conn = open("POST", "/import", contentType = "application/json")
        try {
            conn.doOutput = true
            conn.outputStream.use { it.write(body) }
            return readJson(conn)
        } finally {
            conn.disconnect()
        }
    }

    /** Stream one file straight to disk (exact server filename). False when skipped. */
    fun downloadFile(file: String, library: String, dest: File): Boolean {
        if (file.isBlank() || '/' in file || '\\' in file) return false
        if (dest.exists()) return false
        dest.parentFile?.mkdirs()
        val tmp = File(dest.absolutePath + ".hathor-part")
        val conn = open("GET", "/files/${URLEncoder.encode(file, "UTF-8")}", mapOf("library" to library))
        try {
            val code = try {
                conn.responseCode
            } catch (e: UnknownHostException) {
                throw SyncApiException("Cannot reach sync server at $baseUrl. Is it on your LAN?")
            } catch (e: ConnectException) {
                throw SyncApiException("Cannot reach sync server at $baseUrl. Is it running?")
            }
            if (code == 401) throw SyncApiException("Sync server rejected the API key (401).")
            if (code == 404) throw SyncApiException("Server has no file bytes for $file.")
            if (code !in 200..299) throw SyncApiException("File download failed ($code): $file")
            conn.inputStream.use { input ->
                tmp.outputStream().use { output -> input.copyTo(output) }
            }
        } catch (e: SyncApiException) {
            try { tmp.delete() } catch (_: Exception) { }
            throw e
        } catch (e: Exception) {
            try { tmp.delete() } catch (_: Exception) { }
            Log.w(TAG, "Download failed for $file", e)
            return false
        } finally {
            conn.disconnect()
        }
        if (!tmp.renameTo(dest)) {
            try { tmp.delete() } catch (_: Exception) { }
            Log.w(TAG, "Download: could not rename to $file")
            return false
        }
        return true
    }

    fun putFile(file: String, library: String, bytes: ByteArray) {
        if (file.isBlank() || '/' in file || '\\' in file) return
        val conn = open(
            "PUT", "/files/${URLEncoder.encode(file, "UTF-8")}",
            mapOf("library" to library), "audio/mpeg",
        )
        try {
            conn.doOutput = true
            conn.outputStream.use { it.write(bytes) }
            val code = conn.responseCode
            if (code == 404) throw SyncApiException(serverMissing)
            if (code !in 200..299) throw SyncApiException("File upload failed ($code): $file")
            try { conn.inputStream.close() } catch (_: Exception) { }
        } finally {
            conn.disconnect()
        }
    }

    companion object {
        private const val TAG = "SyncApi"

        private fun JSONObject.optText(key: String): String? =
            if (isNull(key)) null else optString(key).ifEmpty { null }

        private fun isoMillis(raw: String?): Long {
            if (raw.isNullOrBlank()) return System.currentTimeMillis()
            return try {
                java.time.Instant.parse(raw).toEpochMilli()
            } catch (_: Exception) {
                System.currentTimeMillis()
            }
        }

        private fun JSONObject.rows(key: String): List<JSONObject> {
            val arr = optJSONArray(key) ?: return emptyList()
            return buildList {
                for (i in 0 until arr.length()) {
                    val o = arr.optJSONObject(i) ?: continue
                    add(o)
                }
            }
        }

        /** Delta/export payload (camelCase server JSON) -> merge-ready snapshot. */
        fun parseSnapshot(delta: JSONObject): RemoteSnapshot {
            val snap = if (delta.has("snapshot")) delta.optJSONObject("snapshot") ?: JSONObject() else delta
            return RemoteSnapshot(
                songs = snap.rows("songs").map {
                    RemoteSongRow(
                        it.optString("file"), it.optText("downloadedLink"),
                        it.optString("title").ifEmpty { it.optString("file") },
                        isoMillis(it.optText("dateDownloadUtc")), it.optText("artist"),
                    )
                },
                podcasts = snap.rows("podcasts").map {
                    RemoteSongRow(
                        it.optString("file"), it.optText("downloadedLink"),
                        it.optString("title").ifEmpty { it.optString("file") },
                        isoMillis(it.optText("dateDownloadUtc")), it.optText("artist"),
                    )
                },
                playlists = snap.rows("playlists").map {
                    RemotePlaylistRow(
                        it.optLong("id"), it.optString("title"),
                        it.optText("description"), it.optText("thumbnail"),
                    )
                },
                songLinks = snap.rows("songLinks").map {
                    RemoteSongLinkRow(
                        it.optLong("id"), it.optString("songFile"),
                        it.optLong("playlistId"), isoMillis(it.optText("dateAddedUtc")),
                    )
                },
                lyrics = snap.rows("lyrics").map {
                    if ("::chapter:" in it.optString("songFile")) return@map null
                    RemoteLyricRow(
                        it.optLong("id"), it.optString("songFile"),
                        it.optText("lyricsJson"), it.optInt("offsetMs"),
                    )
                }.filterNotNull(),
                musicHistory = snap.rows("musicHistory").map {
                    RemoteMusicHistoryRow(
                        it.optLong("id"), it.optString("songFile"),
                        isoMillis(it.optText("datePlayedUtc")),
                    )
                },
                playlistHistory = snap.rows("playlistHistory").map {
                    RemotePlaylistHistoryRow(
                        it.optLong("id"), it.optLong("playlistId"),
                        isoMillis(it.optText("datePlayedUtc")),
                    )
                },
                tags = snap.rows("podcastTags").map {
                    RemoteTagRow(it.optLong("id"), it.optString("name"))
                },
                tagLinks = snap.rows("podcastTagLinks").map {
                    RemoteTagLinkRow(it.optLong("id"), it.optString("podcastFile"), it.optLong("tagId"))
                },
                chapters = snap.rows("podcastChapters").map {
                    val end = if (it.isNull("endSecs")) null else it.optDouble("endSecs")
                    RemoteChapterRow(
                        it.optLong("id"), it.optString("podcastFile"),
                        it.optString("name"), it.optDouble("startSecs"), end,
                    )
                },
                mixes = snap.rows("dailyMix").map {
                    RemoteMixRow(it.optString("mixDate"), it.optString("songFilesJson", "[]"))
                },
                deletions = snap.rows("deletions").map {
                    RemoteDeletionRow(it.optString("tableName"), it.optString("rowKey"))
                },
            )
        }

        fun missingFiles(result: JSONObject): List<String> {
            val arr: JSONArray = result.optJSONArray("missingFiles")
                ?: result.optJSONArray("missing_files")
                ?: result.optJSONArray("MissingFiles")
                ?: return emptyList()
            return buildList {
                for (i in 0 until arr.length()) {
                    val f = arr.optString(i)
                    if (f.isNotBlank()) add(f)
                }
            }
        }

        fun isoNow(millis: Long): String =
            java.time.Instant.ofEpochMilli(millis).toString()
    }
}
