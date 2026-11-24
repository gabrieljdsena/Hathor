package com.musicplayer.android.data.update

import com.musicplayer.android.data.api.AuthSession
import com.musicplayer.android.data.api.SyncConfig
import org.json.JSONObject
import java.io.File
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.net.UnknownHostException

/**
 * Contract mirror of apps/web/src/Hathor.Api/MobileAppController.cs.
 *
 * The client uses the same LAN base URL + Bearer key as sync. The metadata
 * endpoint is separate from the bytes, so a newer versionCode is known before
 * downloading a ~130 MB APK.
 */
data class UpdateInfo(
    val versionCode: Int,
    val versionName: String,
    val fileName: String,
    val sizeBytes: Long,
    val sha256: String,
    val publishedAtUtc: String?,
    val downloadPath: String,
)

sealed interface LatestResult {
    data class Success(val info: UpdateInfo) : LatestResult
    data object NotFound : LatestResult
    data class Failure(val message: String) : LatestResult
}

class UpdateException(message: String, cause: Throwable? = null) : Exception(message, cause)

interface UpdateApiClient {
    fun latest(): LatestResult

    /** Streams the APK into [destination]. The caller owns verification/rename. */
    fun download(
        info: UpdateInfo,
        destination: File,
        onProgress: (bytes: Long, total: Long) -> Unit,
    )
}

class HttpUpdateApi(
    baseUrl: String,
    private val token: String? = null,
    auth: AuthSession? = null,
) : UpdateApiClient {
    private val base = baseUrl.trim().trimEnd('/')
    private val session = auth ?: if (token == null) SyncConfig.authSession() else null

    private fun currentToken(): String = token ?: session?.accessToken().orEmpty()

    override fun latest(): LatestResult {
        val conn = try {
            open("$base/api/v1/app/latest", "application/json").apply { requestMethod = "GET" }
        } catch (e: Exception) {
            return LatestResult.Failure(networkMessage(e))
        }
        return try {
            val code = try {
                conn.responseCode
            } catch (e: Exception) {
                return LatestResult.Failure(networkMessage(e))
            }
            when {
                code == 401 || code == 403 -> LatestResult.Failure(
                    "Update server rejected the credentials ($code). Sign in or check the API key in Settings.",
                )
                code == 404 -> LatestResult.NotFound
                code !in 200..299 -> LatestResult.Failure(errorMessage(conn, code))
                else -> {
                    val body = try {
                        conn.inputStream.bufferedReader().use { it.readText() }
                    } catch (e: Exception) {
                        return LatestResult.Failure("Update server response could not be read.")
                    }
                    val info = parseLatest(body)
                    if (info == null) {
                        LatestResult.Failure("Update server returned an invalid release response.")
                    } else {
                        LatestResult.Success(info)
                    }
                }
            }
        } finally {
            conn.disconnect()
        }
    }

    override fun download(
        info: UpdateInfo,
        destination: File,
        onProgress: (bytes: Long, total: Long) -> Unit,
    ) {
        val path = info.downloadPath.ifBlank {
            "/api/v1/app/download/${encodePathSegment(info.fileName)}"
        }
        val url = when {
            path.startsWith("http://") || path.startsWith("https://") -> path
            path.startsWith("/") -> base + path
            else -> "$base/$path"
        }
        val conn = try {
            open(url, "application/vnd.android.package-archive").apply { requestMethod = "GET" }
        } catch (e: Exception) {
            throw UpdateException(networkMessage(e), e)
        }
        try {
            val code = try {
                conn.responseCode
            } catch (e: Exception) {
                throw UpdateException(networkMessage(e), e)
            }
            if (code == 401 || code == 403) {
                throw UpdateException("Update server rejected the credentials ($code). Sign in again.")
            }
            if (code == 404) {
                throw UpdateException("The update APK is no longer available on the server.")
            }
            if (code !in 200..299) {
                throw UpdateException(errorMessage(conn, code))
            }
            val total = conn.contentLengthLong.takeIf { it > 0 } ?: info.sizeBytes
            destination.parentFile?.mkdirs()
            var copied = 0L
            var lastReport = 0L
            try {
                conn.inputStream.use { input ->
                    destination.outputStream().use { output ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            val read = input.read(buffer)
                            if (read < 0) break
                            output.write(buffer, 0, read)
                            copied += read
                            val now = System.currentTimeMillis()
                            if (now - lastReport >= 100 || (total > 0 && copied >= total)) {
                                onProgress(copied, total)
                                lastReport = now
                            }
                        }
                    }
                }
            } catch (e: Exception) {
                throw UpdateException("Update download failed: ${e.message ?: "network error"}", e)
            }
            if (total > 0 && copied != total) {
                throw UpdateException("Update download was incomplete ($copied of $total bytes).")
            }
            onProgress(copied, total)
        } finally {
            conn.disconnect()
        }
    }

    private fun open(url: String, accept: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            val bearer = currentToken()
            if (bearer.isNotBlank()) {
                setRequestProperty("Authorization", "Bearer $bearer")
            }
            setRequestProperty("User-Agent", "hathor-android/1")
            setRequestProperty("Accept", accept)
            connectTimeout = 30_000
            // The release APK is ~130 MB; allow a slow LAN transfer.
            readTimeout = 10 * 60_000
        }

    private fun errorMessage(conn: HttpURLConnection, code: Int): String {
        val detail = try {
            JSONObject(conn.errorStream?.bufferedReader()?.readText().orEmpty()).optString("message")
        } catch (_: Exception) {
            ""
        }
        return "Update server error ($code)${if (detail.isBlank()) "" else ": $detail"}"
    }

    private fun networkMessage(e: Exception): String = when (e) {
        is UnknownHostException, is ConnectException ->
            "Cannot reach the update server at $base. Is it on your LAN?"
        else -> "Cannot reach the update server: ${e.message ?: e.javaClass.simpleName}"
    }

    companion object {
        private val SHA256_HEX = Regex("^[0-9a-f]{64}$")

        /** Decode and validate the server's release metadata. */
        internal fun parseLatest(body: String): UpdateInfo? {
            val obj = try {
                JSONObject(body)
            } catch (_: Exception) {
                return null
            }
            val versionCode = obj.optInt("versionCode", 0)
            val versionName = obj.optString("versionName").trim()
            val fileName = obj.optString("fileName").trim()
            val sizeBytes = obj.optLong("sizeBytes", 0L)
            val sha256 = obj.optString("sha256").trim().lowercase()
            val downloadPath = obj.optString("downloadPath").trim()
            if (versionCode <= 0 || versionName.isBlank()) return null
            if (sizeBytes <= 0) return null
            if (!SHA256_HEX.matches(sha256)) return null
            if (fileName.length > 255 || fileName.contains('/') || fileName.contains('\\') ||
                !fileName.endsWith(".apk", ignoreCase = true)
            ) {
                return null
            }
            if (downloadPath.isBlank()) return null
            return UpdateInfo(
                versionCode = versionCode,
                versionName = versionName,
                fileName = fileName,
                sizeBytes = sizeBytes,
                sha256 = sha256,
                publishedAtUtc = obj.optString("publishedAtUtc").trim().takeIf { it.isNotBlank() },
                downloadPath = downloadPath,
            )
        }

        private fun encodePathSegment(value: String): String =
            URLEncoder.encode(value, "UTF-8").replace("+", "%20")
    }
}
