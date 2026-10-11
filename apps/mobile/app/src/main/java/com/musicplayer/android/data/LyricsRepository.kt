package com.musicplayer.android.data

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.db.LyricEntity
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
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
            val cached = db.lyricDao().bySongFile(fileName)?.lyrics
            if (!cached.isNullOrBlank()) {
                _state.value = LyricsState.Ready(toResult(true, cached))
                return
            }
            val fetched = fetchExact(title, artist, durationSec)
                ?: fetchTrackOnly(title, durationSec)
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
        return try {
            val q = "track_name=" + URLEncoder.encode(track, "UTF-8") +
                "&artist_name=" + URLEncoder.encode(artist, "UTF-8") +
                "&album_name=" + URLEncoder.encode(album, "UTF-8")
            val arr = org.json.JSONArray(get("https://lrclib.net/api/search?$q"))
            buildList {
                for (i in 0 until minOf(arr.length(), 30)) {
                    val o = arr.getJSONObject(i)
                    if (o.optBoolean("instrumental", false)) continue
                    add(
                        LyricSuggestion(
                            id = o.optLong("id", 0),
                            trackName = o.optString("trackName", ""),
                            artistName = o.optString("artistName", ""),
                            albumName = o.optString("albumName", ""),
                            durationSec = o.optDouble("duration", 0.0).toInt(),
                            plainLyrics = o.optString("plainLyrics").takeIf { it.isNotBlank() },
                            syncedLyrics = o.optString("syncedLyrics").takeIf { it.isNotBlank() },
                        ),
                    )
                    if (size >= 10) break
                }
            }
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

    suspend fun loadOffset(fileName: String): Int {
        return try {
            (db.lyricDao().bySongFile(fileName)?.offsetMs ?: 0).coerceIn(-20000, 20000)
        } catch (_: Exception) {
            0
        }
    }

    suspend fun saveOffset(fileName: String, ms: Int): Int {
        val clamped = ms.coerceIn(-20000, 20000)
        try {
            val dao = db.lyricDao()
            val row = dao.bySongFile(fileName)
            if (row == null) {
                dao.insertIgnore(LyricEntity(songFile = fileName, lyrics = null, offsetMs = clamped))
            } else {
                dao.update(row.copy(offsetMs = clamped))
            }
        } catch (e: Exception) {
            Log.w(TAG, "offset save failed", e)
        }
        return clamped
    }

    private suspend fun saveRow(fileName: String, synced: String?, plain: String?) {
        val dao = db.lyricDao()
        val existing = dao.bySongFile(fileName)
        val json = cachedJson(synced, plain)
        if (existing == null) {
            dao.insertIgnore(LyricEntity(songFile = fileName, lyrics = json))
        } else {
            dao.update(existing.copy(lyrics = json))
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

    private fun cleanTrackArtist(track: String, artist: String): Pair<String, String> {
        var t = track.split(" - ").firstOrNull()?.trim().orEmpty()
        t = t.replace(Regex("(?i)\\s*[\\(\\[].*?(remaster|mix|live|feat|edit|version).*?[\\]\\)]"), "").trim()
        return t to artist.trim()
    }

    private fun artistUsable(artist: String): Boolean =
        artist.isNotBlank() && !artist.equals("Unknown", ignoreCase = true) && artist != "?"

    private suspend fun fetchExact(title: String, artist: String, durationSec: Int): Pair<String?, String?>? {
        val (track, cleanArtist) = cleanTrackArtist(title, artist)
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
        val (track, _) = cleanTrackArtist(title, "")
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
