package com.musicplayer.android.data.update

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.URL
import java.net.UnknownHostException

/**
 * One published client release. The download URL is the public GitHub asset
 * URL; the updater verifies the SHA-256 before offering it to Android.
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

/**
 * Public GitHub Releases update source.
 *
 * The repository is public, so no Web API login or API key is needed. The
 * dev channel includes prereleases until stable releases are cut from main;
 * switch [includePrereleases] to false for a stable channel later.
 */
class GitHubUpdateApi(
    private val releasesUrl: String = DEFAULT_RELEASES_URL,
    private val includePrereleases: Boolean = true,
) : UpdateApiClient {

    override fun latest(): LatestResult {
        val conn = try {
            open(releasesUrl).apply { requestMethod = "GET" }
        } catch (e: Exception) {
            return LatestResult.Failure(networkMessage(e))
        }
        return try {
            val code = try {
                conn.responseCode
            } catch (e: Exception) {
                return LatestResult.Failure(networkMessage(e))
            }
            if (code == 404) return LatestResult.NotFound
            if (code !in 200..299) {
                return LatestResult.Failure("GitHub release feed returned HTTP $code.")
            }
            val body = try {
                conn.inputStream.bufferedReader().use { it.readText() }
            } catch (e: Exception) {
                return LatestResult.Failure("GitHub release feed could not be read.")
            }
            val releases = try {
                JSONArray(body)
            } catch (e: Exception) {
                return LatestResult.Failure("GitHub returned an invalid release feed.")
            }
            parseReleases(releases)
        } finally {
            conn.disconnect()
        }
    }

    internal fun parseReleases(releases: JSONArray): LatestResult {
        var best: UpdateInfo? = null
        for (i in 0 until releases.length()) {
            val release = releases.optJSONObject(i) ?: continue
            if (release.optBoolean("draft")) continue
            if (!includePrereleases && release.optBoolean("prerelease")) continue
            val tag = release.optString("tag_name").trim()
            if (!tag.startsWith(ANDROID_TAG_PREFIX)) continue
            val assets = release.optJSONArray("assets") ?: continue
            for (j in 0 until assets.length()) {
                val asset = assets.optJSONObject(j) ?: continue
                val name = asset.optString("name").trim()
                val match = APK_RE.matchEntire(name) ?: continue
                val code = match.groupValues[1].toIntOrNull() ?: continue
                if (code <= 0) continue
                val size = asset.optLong("size", 0L)
                val url = asset.optString("browser_download_url").trim()
                val sha = assetSha(asset, assets) ?: continue
                if (size <= 0 || !trustedUrl(url) || !SHA_RE.matches(sha)) continue
                val candidate = UpdateInfo(
                    versionCode = code,
                    versionName = match.groupValues[2],
                    fileName = name,
                    sizeBytes = size,
                    sha256 = sha,
                    publishedAtUtc = release.optString("published_at").trim().takeIf { it.isNotBlank() },
                    downloadPath = url,
                )
                if (best == null || code > best.versionCode) best = candidate
            }
        }
        return best?.let { LatestResult.Success(it) } ?: LatestResult.NotFound
    }

    override fun download(
        info: UpdateInfo,
        destination: File,
        onProgress: (bytes: Long, total: Long) -> Unit,
    ) {
        if (!trustedUrl(info.downloadPath)) {
            throw UpdateException("GitHub release asset URL is invalid.")
        }
        val conn = try {
            open(info.downloadPath).apply { requestMethod = "GET" }
        } catch (e: Exception) {
            throw UpdateException(networkMessage(e), e)
        }
        try {
            val code = try {
                conn.responseCode
            } catch (e: Exception) {
                throw UpdateException(networkMessage(e), e)
            }
            if (code == 404) {
                throw UpdateException("The GitHub release asset is no longer available.")
            }
            if (code !in 200..299) {
                throw UpdateException("GitHub download failed (HTTP $code).")
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
                throw UpdateException(
                    "Update download failed: ${e.message ?: "network error"}",
                    e,
                )
            }
            if (total > 0 && copied != total) {
                throw UpdateException("Update download was incomplete ($copied of $total bytes).")
            }
            onProgress(copied, total)
        } finally {
            conn.disconnect()
        }
    }

    private fun open(url: String): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 20_000
            readTimeout = 120_000
            setRequestProperty("Accept", "application/vnd.github+json")
            setRequestProperty("User-Agent", "hathor-android/1")
            setRequestProperty("X-GitHub-Api-Version", "2022-11-28")
        }

    /**
     * Production feeds are HTTPS and only accept HTTPS assets. Local test
     * servers are HTTP; allow matching HTTP asset URLs only in that case.
     */
    private fun trustedUrl(url: String): Boolean =
        url.startsWith("https://") ||
            (!releasesUrl.startsWith("https://") && url.startsWith("http://"))

    private fun assetSha(asset: JSONObject, assets: JSONArray): String? {
        val digest = asset.optString("digest").trim().lowercase()
        if (digest.startsWith("sha256:")) {
            val sha = digest.removePrefix("sha256:")
            if (SHA_RE.matches(sha)) return sha
        }
        // Older GitHub assets may not expose a digest yet: fall back to the
        // SHA256SUMS.txt asset published by the release workflow.
        val sums = findAsset(assets, "SHA256SUMS.txt") ?: return null
        val sumsUrl = sums.optString("browser_download_url").trim()
        if (!trustedUrl(sumsUrl)) return null
        val text = try {
            fetchText(sumsUrl)
        } catch (_: Exception) {
            return null
        }
        return parseSums(text, asset.optString("name"))
    }

    private fun findAsset(assets: JSONArray, name: String): JSONObject? {
        for (i in 0 until assets.length()) {
            val asset = assets.optJSONObject(i) ?: continue
            if (asset.optString("name") == name) return asset
        }
        return null
    }

    private fun fetchText(url: String): String {
        val conn = open(url).apply { requestMethod = "GET" }
        return try {
            val code = conn.responseCode
            if (code !in 200..299) throw IllegalStateException("HTTP $code")
            conn.inputStream.bufferedReader().use { it.readText() }
        } finally {
            conn.disconnect()
        }
    }

    private fun networkMessage(e: Exception): String = when (e) {
        is UnknownHostException, is ConnectException ->
            "Cannot reach GitHub Releases. Check the internet connection."
        else -> "Cannot reach GitHub Releases: ${e.message ?: e.javaClass.simpleName}"
    }

    companion object {
        const val DEFAULT_RELEASES_URL =
            "https://api.github.com/repos/gabrieljdsena/Hathor/releases?per_page=30"
        private const val ANDROID_TAG_PREFIX = "android-v"
        private val APK_RE = Regex(
            "^hathor-(?:android-)?(\\d{1,9})-(.+)\\.apk$",
            RegexOption.IGNORE_CASE,
        )
        private val SHA_RE = Regex("^[0-9a-f]{64}$")
        private val SUM_RE = Regex("^([0-9a-fA-F]{64})\\s+\\*?(.+)$")

        /** Parse `sha256sum` output (used by the SHA256SUMS.txt asset). */
        internal fun parseSums(text: String, fileName: String): String? {
            for (line in text.lineSequence()) {
                val match = SUM_RE.matchEntire(line.trim()) ?: continue
                val listed = match.groupValues[2].trim().removePrefix("./")
                if (listed == fileName) {
                    val sha = match.groupValues[1].lowercase()
                    if (SHA_RE.matches(sha)) return sha
                }
            }
            return null
        }
    }
}
