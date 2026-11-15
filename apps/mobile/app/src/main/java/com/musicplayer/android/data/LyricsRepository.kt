package com.musicplayer.android.data

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.api.HathorApi
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.LyricEntity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

// Lyrics cache + lrclib fetch (desktop services/lyrics.py, 1:1): Room cache
// first, lrclib exact endpoint, track-only exact-match fallback for
// artist-less songs, suggestions capped at 10 skipping instrumentals.
// Highlight offset (desktop offset_ms / web ±20000ms) rides the same row.
data class LyricSuggestion(
    val id: Long,
    val trackName: String,
    val artistName: String,
    val albumName: String,
    val durationSec: Int,
    val plainLyrics: String?,
    val syncedLyrics: String?,
)

private val BracketQualifier = Regex(
    """\s*[\(\[].*?(remaster|mix|version|edit|live|feat\.|ft\.).*?[\)\]]""",
    RegexOption.IGNORE_CASE
)

/** Web LyricsCleaning.CleanTrackArtist (bracket qualifiers, Artist - Title split). */
internal fun cleanLyricsQuery(trackName: String, artistName: String): Pair<String, String> {
    var track = trackName.replace('\uFF1F', '?').replace('\uFF01', '!')
    track = BracketQualifier.replace(track, "").trim()
    var artist = artistName
    val unknown = artist.isBlank() ||
        artist.trim().lowercase() in setOf("unknown", "unknown artist")
    if (unknown && track.contains(" - ")) {
        val parts = track.split(" - ", limit = 2)
        if (parts.size == 2) {
            artist = parts[0].trim()
            track = parts[1].trim()
        }
    }
    return track to artist
}

/** Web SplitChapterName: chapters are "Title - Artist" (opposite of filenames). */
internal fun splitLyricsChapter(name: String): Pair<String, String> {
    val title = BracketQualifier.replace(name, "").trim()
    val dash = title.indexOf(" - ")
    if (dash < 0) return title to ""
    return title.substring(0, dash).trim() to title.substring(dash + 3).trim()
}

private fun JSONObject.textOrNull(name: String): String? {
    if (!has(name) || isNull(name)) return null
    val value = optString(name).trim()
    return value.takeIf { it.isNotEmpty() && !it.equals("null", ignoreCase = true) }
}

/** Mirrors the web server's lrclib result filtering. */
internal fun parseLyricsSearchHits(arr: JSONArray): List<LyricSuggestion> {
    val hits = mutableListOf<LyricSuggestion>()
    val seen = HashSet<String>()
    for (i in 0 until arr.length()) {
        val o = arr.optJSONObject(i) ?: continue
        if (o.optBoolean("instrumental", false)) continue
        val synced = o.textOrNull("syncedLyrics")
        val plain = o.textOrNull("plainLyrics")
        if (synced == null && plain == null) continue
        val trackName = o.textOrNull("trackName").orEmpty()
        val artistName = o.textOrNull("artistName").orEmpty()
        if (!seen.add("$trackName\n$artistName")) continue
        hits.add(
            LyricSuggestion(
                id = o.optLong("id", 0),
                trackName = trackName,
                artistName = artistName,
                albumName = o.textOrNull("albumName").orEmpty(),
                durationSec = o.optDouble("duration", 0.0).toInt(),
                plainLyrics = plain,
                syncedLyrics = synced,
            )
        )
        if (hits.size >= 10) break
    }
    return hits
}

class LyricsRepository(context: Context) {
    private val appCtx = context.applicationContext
    private val db by lazy { AppDatabase.get(appCtx) }

    data class LyricResult(
        val fromCache: Boolean,
        val synced: String?,
        val plain: String?,
        val displayText: String?,
    )

    sealed interface LyricsState {
        data object Idle : LyricsState
        data object Loading : LyricsState
        data class Ready(val result: LyricResult) : LyricsState
        data class Failed(val message: String) : LyricsState
    }

    private val _state = MutableStateFlow<LyricsState>(LyricsState.Idle)
    val state: StateFlow<LyricsState> = _state.asStateFlow()

    fun reset() {
        _state.value = LyricsState.Idle
    }

    suspend fun loadFor(
        fileName: String,
        title: String,
        artist: String,
        album: String,
        durationSec: Int,
    ) {
        _state.value = LyricsState.Loading
        try {
            val cached = db.playlistDao().lyricFor(fileName)?.lyrics
            if (!cached.isNullOrBlank()) {
                _state.value = LyricsState.Ready(toResult(true, cached))
                return
            }
            val fetched = withContext(Dispatchers.IO) {
                fetchExact(title, artist, durationSec)
                    ?: fetchTrackOnly(title, durationSec)
            }
            if (fetched != null) {
                saveRow(fileName, fetched.first, fetched.second)
                _state.value = LyricsState.Ready(toResult(false, cachedJson(fetched.first, fetched.second)))
            } else {
                _state.value = LyricsState.Failed("No lyrics found for this song.")
            }
        } catch (e: Exception) {
            Log.w(TAG, "lyrics load failed", e)
            _state.value = LyricsState.Failed("Failed to load lyrics.")
        }
    }

    suspend fun searchSuggestions(
        track: String,
        artist: String,
        album: String,
        durationSec: Int,
    ): List<LyricSuggestion> {
        // Same source as the web UI: POST /lyrics/search. The server applies
        // LyricsCleaning + lrclib's instrumental/empty/dedupe filters. Only
        // fall back to lrclib directly when the Web API is unavailable.
        if (SyncConfig.isConfigured()) {
            try {
                val body = JSONObject()
                    .put("Track", track)
                    .put("Artist", artist)
                val hits = parseLyricsSearchHits(HathorApi().postArray("/lyrics/search", body))
                Log.i(TAG, "lyrics search via Web API: ${hits.size} hit(s)")
                return hits
            } catch (e: Exception) {
                Log.w(TAG, "server lyrics search failed, falling back to lrclib", e)
            }
        }
        val (cleanTrack, cleanArtist) = cleanLyricsQuery(track, artist)
        Log.i(TAG, "lyrics search via lrclib fallback")
        return withContext(Dispatchers.IO) {
            directLrclibSearch(cleanTrack, cleanArtist, album)
        }
    }

    /** Mirrors the web server's lrclib result filtering. */
    private fun directLrclibSearch(
        track: String,
        artist: String,
        album: String,
    ): List<LyricSuggestion> {
        return try {
            val q = "track_name=" + URLEncoder.encode(track, "UTF-8") +
                "&artist_name=" + URLEncoder.encode(artist, "UTF-8") +
                "&album_name=" + URLEncoder.encode(album, "UTF-8")
            parseLyricsSearchHits(JSONArray(get("https://lrclib.net/api/search?$q")))
        } catch (e: Exception) {
            Log.w(TAG, "lyrics search failed", e)
            emptyList()
        }
    }

    suspend fun saveLyrics(fileName: String, plain: String?, synced: String?): Boolean {
        if (plain.isNullOrBlank() && synced.isNullOrBlank()) return false
        return try {
            saveRow(fileName, synced, plain)
            _state.value = LyricsState.Ready(
                LyricResult(false, synced, plain, plain ?: synced),
            )
            true
        } catch (e: Exception) {
            Log.w(TAG, "lyrics save failed", e)
            false
        }
    }

    /** Remove cached lyrics + offset for one file (web lyrics delete). */
    suspend fun deleteLyrics(fileName: String): Boolean {
        return try {
            db.playlistDao().deleteLyricsForSong(fileName)
            _state.value = LyricsState.Failed("Lyrics removed from this device.")
            true
        } catch (e: Exception) {
            Log.w(TAG, "lyrics delete failed", e)
            false
        }
    }

    suspend fun loadOffset(fileName: String): Int {        return try {
            (db.playlistDao().lyricFor(fileName)?.offsetMs ?: 0).coerceIn(-20000, 20000)
        } catch (_: Exception) {
            0
        }
    }

    suspend fun saveOffset(fileName: String, ms: Int): Int {
        val clamped = ms.coerceIn(-20000, 20000)
        try {
            val dao = db.playlistDao()
            val row = dao.lyricFor(fileName)
            if (row == null) {
                dao.insertLyricIgnore(LyricEntity(songFile = fileName, lyrics = null, offsetMs = clamped))
            } else {
                dao.updateLyricEntity(row.copy(offsetMs = clamped))
            }
        } catch (e: Exception) {
            Log.w(TAG, "offset save failed", e)
        }
        return clamped
    }

    private suspend fun saveRow(fileName: String, synced: String?, plain: String?) {
        val dao = db.playlistDao()
        val existing = dao.lyricFor(fileName)
        val json = cachedJson(synced, plain)
        if (existing == null) {
            dao.insertLyricIgnore(LyricEntity(songFile = fileName, lyrics = json))
        } else {
            dao.updateLyricEntity(existing.copy(lyrics = json))
        }
    }

    private fun toResult(fromCache: Boolean, json: String): LyricResult {
        return try {
            val o = JSONObject(json)
            val synced = o.optString("synced").takeIf { it.isNotBlank() }
            val plain = o.optString("plain").takeIf { it.isNotBlank() }
            LyricResult(fromCache, synced, plain, plain ?: synced)
        } catch (_: Exception) {
            LyricResult(fromCache, null, json.takeIf { it.isNotBlank() }, json.takeIf { it.isNotBlank() })
        }
    }

    private fun cachedJson(synced: String?, plain: String?): String =
        JSONObject().put("synced", synced).put("plain", plain).toString()

    private fun artistUsable(artist: String): Boolean =
        artist.isNotBlank() && !artist.equals("Unknown", ignoreCase = true) && artist != "?"

    private suspend fun fetchExact(title: String, artist: String, durationSec: Int): Pair<String?, String?>? {
        val (track, cleanArtist) = cleanLyricsQuery(title, artist)
        if (track.isBlank() || !artistUsable(cleanArtist)) return null
        return try {
            val q = "track_name=" + URLEncoder.encode(track, "UTF-8") +
                "&artist_name=" + URLEncoder.encode(cleanArtist, "UTF-8") +
                "&duration=" + durationSec
            val o = JSONObject(get("https://lrclib.net/api/get?$q"))
            if (o.optBoolean("instrumental", false)) return null
            o.optString("syncedLyrics").takeIf { it.isNotBlank() } to
                o.optString("plainLyrics").takeIf { it.isNotBlank() }
        } catch (_: Exception) {
            null
        }
    }

    private suspend fun fetchTrackOnly(title: String, durationSec: Int): Pair<String?, String?>? {
        val (track, _) = cleanLyricsQuery(title, "")
        if (track.isBlank()) return null
        return try {
            val hits = searchSuggestions(track, "", "", durationSec)
            val exact = hits.firstOrNull {
                it.trackName.equals(track, ignoreCase = true) && !it.syncedLyrics.isNullOrBlank()
            } ?: return null
            exact.syncedLyrics to exact.plainLyrics
        } catch (_: Exception) {
            null
        }
    }

    private fun get(url: String): String {
        val conn = URL(url).openConnection() as HttpURLConnection
        return try {
            conn.connectTimeout = 15000
            conn.readTimeout = 15000
            conn.setRequestProperty("User-Agent", "Hathor-Android/1.0")
            if (conn.responseCode !in 200..299) throw IllegalStateException("HTTP ${conn.responseCode}")
            conn.inputStream.bufferedReader().use { it.readText() }
        } finally {
            conn.disconnect()
        }
    }

    companion object {
        private const val TAG = "LyricsRepository"
    }
}
