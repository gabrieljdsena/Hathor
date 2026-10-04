package com.musicplayer.android.engine

import android.content.Context
import android.os.Environment
import android.util.Log
import com.yausername.youtubedl_android.YoutubeDL
import com.yausername.youtubedl_android.YoutubeDLRequest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Phase 1 "prove the engine" wrapper.
 *
 * Mirrors the desktop Download.py semantics: bestaudio/best -> extract audio
 * -> mp3, written to an app-specific Music dir (no storage permission needed,
 * no SAF yet — that is Phase 3).
 */
class DownloadEngine(private val context: Context) {

    private val _progress = MutableStateFlow<Float?>(null)
    val progress: StateFlow<Float?> = _progress.asStateFlow()

    private val _logLines = MutableStateFlow<List<String>>(emptyList())
    val logLines: StateFlow<List<String>> = _logLines.asStateFlow()

    fun outputDir(): File {
        // Single choke point: the active folder lives in PlayerApp (default
        // app-private, or the user-picked SAF folder from Settings).
        val dir = (context.applicationContext as com.musicplayer.android.PlayerApp).musicDir
        if (!dir.exists()) dir.mkdirs()
        return dir
    }

    /** Second library dir (desktop podcasts_path): podcast downloads land here. */
    fun podcastsDir(): File {
        val dir = (context.applicationContext as com.musicplayer.android.PlayerApp).podcastsDir
        if (!dir.exists()) dir.mkdirs()
        return dir
    }

    suspend fun downloadToMp3(
        url: String,
        onProgress: ((Float) -> Unit)? = null,
        processId: String? = null,
        outDir: File? = null
    ): Result<File> = withContext(Dispatchers.IO) {
        // Per-call reporter (queue jobs pass their own); default keeps the
        // shared flow the single-download screen observes.
        val report: (Float) -> Unit = onProgress ?: { _progress.value = it }
        val cleanUrl = url.trim()
        if (cleanUrl.isEmpty()) {
            return@withContext Result.failure(IllegalArgumentException("Empty URL"))
        }
        // Desktop Download.py: bare text becomes a ytsearch1: query, not a URL.
        // Desktop always appends " audio" for non-URLs (download_song:162).
        val target = if (cleanUrl.startsWith("http", ignoreCase = true) ||
            cleanUrl.startsWith("ytsearch", ignoreCase = true)
        ) {
            cleanUrl
        } else {
            "ytsearch1:$cleanUrl audio"
        }
        val outDir = outDir ?: outputDir()
        appendLog("Output: ${outDir.absolutePath}")

        val request = YoutubeDLRequest(target)
        request.addOption("--no-playlist")
        request.addOption("-f", "bestaudio/best")
        request.addOption("-x")
        request.addOption("--audio-format", "mp3")
        // Desktop parity (Download.py outtmpl): id-prefixed names so rows
        // match across desktop and Android libraries; restrictfilenames so
        // both sides sanitize identically.
        request.addOption("--restrict-filenames")
        request.addOption("-o", outDir.absolutePath + "/%(id)s_%(title)s.%(ext)s")

        _progress.value = 0f
        report(0f)
        try {
            appendLog("Starting: $cleanUrl")
            val before = outDir.listFiles()?.toSet() ?: emptySet()
            // Log only on integer-percent change: the native callback fires
            // many times per second, and each line is a StateFlow emission.
            var lastPct = -1
            val progressCb: (Float, Long, String) -> Unit = { p, eta, _ ->
                // yt-dlp can emit >100% (merging/post-process pass, unknown
                // total size). Clamp to 0..1 so callers never see 9000%.
                val frac = (p / 100f).coerceIn(0f, 1f)
                _progress.value = frac
                report(frac)
                val pct = (frac * 100).toInt()
                if (eta >= 0 && pct != lastPct) {
                    lastPct = pct
                    appendLog("… $pct% (ETA ${eta}s)")
                }
            }
            if (processId != null) {
                YoutubeDL.getInstance().execute(request, processId, progressCb)
            } else {
                YoutubeDL.getInstance().execute(request) { p, eta, msg ->
                    progressCb(p, eta, msg)
                }
            }
            val after = outDir.listFiles()?.toSet() ?: emptySet()
            val freshMp3 = (after - before)
                .filter { it.isFile && it.extension.equals("mp3", ignoreCase = true) && it.length() > 0 }
                .maxByOrNull { it.lastModified() }
            // No fallback to pre-existing files: attributing a stale file as
            // the result corrupts the queue record AND any auto-tag written
            // onto it. No new file = failed download, stated plainly.
            if (freshMp3 == null) {
                val msg = "yt-dlp finished but produced no new audio file in ${outDir.absolutePath}"
                appendLog("FAILED: $msg")
                _progress.value = null
                report(0f)
                return@withContext Result.failure(IllegalStateException(msg))
            }
            _progress.value = 1f
            report(1f)
            // Guard for search-originated downloads (sync linkless rows, text
            // searches): a 20+ minute result for a song is a wrong pick
            // (compilation/loop/live set), not the track. Direct links bypass
            // this — the user chose that exact video. Rejects delete the file.
            if (isSearchQuery(target)) {
                val tooLong = isSuspiciouslyLong(freshMp3, MAX_SEARCH_MINUTES)
                if (tooLong == true) {
                    appendLog("FAILED: ${freshMp3.name} is over $MAX_SEARCH_MINUTES min — likely a wrong match, deleted")
                    _progress.value = null
                    try {
                        freshMp3.delete()
                    } catch (_: Exception) {
                    }
                    return@withContext Result.failure(
                        IllegalStateException(
                            "Skipped: video is over $MAX_SEARCH_MINUTES min, likely not the song. Use a direct link."
                        )
                    )
                }
            }
            appendLog("DONE: ${freshMp3.name} (${freshMp3.length()} bytes)")
            Result.success(freshMp3)
        } catch (e: Exception) {
            Log.e(TAG, "Download failed", e)
            appendLog("FAILED: ${e.message ?: e.toString()}")
            _progress.value = null
            Result.failure(e)
        }
    }

    fun reset() {
        _progress.value = null
        _logLines.value = emptyList()
    }

    private fun isSearchQuery(target: String): Boolean {
        val t = target.trim()
        return t.startsWith("ytsearch", ignoreCase = true) ||
            (!t.startsWith("http", ignoreCase = true))
    }

    /**
     * True when the file's audio duration positively exceeds the limit.
     * Null (unknown/unreadable) allows the file — never nuke on a guess.
     */
    private fun isSuspiciouslyLong(file: File, maxMinutes: Int): Boolean? {
        return try {
            android.media.MediaMetadataRetriever().use { r ->
                r.setDataSource(file.absolutePath)
                val ms = r.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_DURATION)
                    ?.toLongOrNull() ?: return null
                ms > maxMinutes * 60_000L
            }
        } catch (_: Exception) {
            null
        }
    }

    /** Kill a running yt-dlp process (cancel button / stuck watchdog). */
    fun cancelDownload(processId: String): Boolean {
        return try {
            YoutubeDL.getInstance().destroyProcessById(processId)
        } catch (_: Exception) {
            false
        }
    }

    /**
     * Direct audio stream URL for preview playback (`yt-dlp -g`): no file is
     * written, nothing is transcoded. Any best-audio codec goes — previews
     * play through ExoPlayer, which handles opus/webm as well as m4a.
     * Pass a processId to make the resolve cancellable via [cancelDownload].
     */
    suspend fun getAudioStreamUrl(url: String, processId: String? = null): Result<String> =
        withContext(Dispatchers.IO) {
            val target = url.trim()
            if (target.isEmpty()) {
                return@withContext Result.failure(IllegalArgumentException("Empty URL"))
            }
            try {
                val req = YoutubeDLRequest(target)
                req.addOption("--no-playlist")
                req.addOption("--no-warnings")
                req.addOption("-f", "bestaudio/best")
                req.addOption("-g")
                val resp = if (processId != null) {
                    YoutubeDL.getInstance().execute(req, processId)
                } else {
                    YoutubeDL.getInstance().execute(req)
                }
                if (resp.exitCode != 0) {
                    return@withContext Result.failure(
                        IllegalStateException("Stream resolve failed: ${resp.err.take(300)}")
                    )
                }
                val stream = resp.out.lineSequence()
                    .map { it.trim() }
                    .firstOrNull { it.startsWith("http") }
                    ?: return@withContext Result.failure(
                        IllegalStateException("No playable stream found")
                    )
                Result.success(stream)
            } catch (e: CancellationException) {
                // Preview resolve cancelled (switch/stop): must rethrow —
                // Result.failure() forbids CancellationException.
                throw e
            } catch (e: Exception) {
                Result.failure(e)
            }
        }

    /**
     * Desktop Download.search_yt(): flat `ytsearch{limit}:` dump, parsed from
     * the process stdout (the wrapper's VideoInfo mapper has no entries
     * field, so --dump-single-json + manual parse mirrors extract_flat).
     * Result shape matches desktop: id/title/uploader/duration/thumbnail.
     */
    suspend fun searchYouTube(query: String, limit: Int = 5): Result<List<YtResult>> =
        withContext(Dispatchers.IO) {
            val q = query.trim()
            if (q.isEmpty()) {
                return@withContext Result.failure(IllegalArgumentException("Empty search"))
            }
            try {
                val req = YoutubeDLRequest("ytsearch${limit.coerceIn(1, 10)}:$q")
                req.addOption("--dump-single-json")
                req.addOption("--flat-playlist")
                req.addOption("--skip-download")
                val resp = YoutubeDL.getInstance().execute(req)
                if (resp.exitCode != 0) {
                    return@withContext Result.failure(
                        IllegalStateException("Search failed: ${resp.err.take(300)}")
                    )
                }
                val entries = org.json.JSONObject(resp.out).optJSONArray("entries")
                    ?: return@withContext Result.success(emptyList())
                val out = mutableListOf<YtResult>()
                for (i in 0 until entries.length()) {
                    val e = entries.optJSONObject(i) ?: continue
                    val id = e.optString("id", "")
                    if (id.isEmpty()) continue
                    val thumbs = e.optJSONArray("thumbnails")
                    val thumb = if (thumbs != null && thumbs.length() > 0) {
                        thumbs.optJSONObject(thumbs.length() - 1)?.optString("url", "").orEmpty()
                    } else ""
                    out.add(
                        YtResult(
                            id = id,
                            title = e.optString("title", "Unknown"),
                            uploader = e.optString("uploader", e.optString("channel", "")),
                            durationSec = e.optLong("duration", 0L),
                            thumbnail = thumb,
                            url = e.optString("webpage_url", "").ifBlank {
                                "https://www.youtube.com/watch?v=$id"
                            }
                        )
                    )
                }
                Result.success(out)
            } catch (e: Exception) {
                Result.failure(e)
            }
        }

    private fun appendLog(line: String) {
        _logLines.value = (_logLines.value + line).takeLast(200)
        Log.i(TAG, line)
    }

    companion object {
        private const val TAG = "DownloadEngine"

        /** Search downloads longer than this are treated as wrong picks. */
        const val MAX_SEARCH_MINUTES = 15

        /**
         * The linkless-row query both apps use: title + artist + audio.
         * When the artist is missing, recover it from an "Artist - Title"
         * title first (same split lyrics cleaning uses) instead of
         * searching title-only, which is where first-hit quality collapses.
         */
        fun searchQuery(title: String, artist: String?): String {
            var t = title.trim()
            var a = (artist ?: "").trim()
            if ((a.isEmpty() || a.equals("Unknown", ignoreCase = true)) && " - " in t) {
                val parts = t.split(" - ", limit = 2)
                a = parts[0].trim()
                t = parts[1].trim()
            }
            val who = a.takeIf { it.isNotEmpty() && !it.equals("Unknown", ignoreCase = true) }
            return "ytsearch1:$t ${who ?: ""} audio".trim().replace(Regex("\\s+"), " ")
        }
    }
}

data class YtResult(
    val id: String,
    val title: String,
    val uploader: String,
    val durationSec: Long,
    val thumbnail: String,
    val url: String
)
