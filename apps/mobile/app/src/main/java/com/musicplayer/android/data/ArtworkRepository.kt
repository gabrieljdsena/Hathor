package com.musicplayer.android.data

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * Port of desktop AppleService (services/apple.py): artist/album artwork via
 * the Apple iTunes Search API. Same queries, same match preference (exact
 * album+artist, then exact album, then Apple's top pick; exact artist name,
 * else top artist id -> most-popular album art), same 600x600 upgrade.
 * Memory-cached per session; desktop doesn't persist these either.
 */
class ArtworkRepository {

    private val albumCache = HashMap<String, String?>()
    private val artistCache = HashMap<String, String?>()

    suspend fun albumArt(album: String, artist: String?): String? = withContext(Dispatchers.IO) {
        val cleanAlbum = album.trim()
        if (cleanAlbum.isEmpty() || cleanAlbum == "Unknown") return@withContext null
        val cleanArtist = artist?.trim().orEmpty().takeIf { it.isNotEmpty() && it != "Unknown" }
        val key = "$cleanAlbum||${cleanArtist ?: ""}"
        synchronized(albumCache) { if (albumCache.containsKey(key)) return@withContext albumCache[key] }
        val term = if (cleanArtist != null) "$cleanAlbum $cleanArtist" else cleanAlbum
        val data = getJson("https://itunes.apple.com/search?term=${enc(term)}&entity=album&limit=10")
        val url = pickAlbumArtwork(data, cleanAlbum, cleanArtist)
        synchronized(albumCache) { albumCache[key] = url }
        url
    }

    suspend fun artistImage(artist: String): String? = withContext(Dispatchers.IO) {        val clean = artist.trim()
        if (clean.isEmpty() || clean == "Unknown") return@withContext null
        synchronized(artistCache) { if (artistCache.containsKey(clean)) return@withContext artistCache[clean] }
        var url: String? = null
        val data = getJson("https://itunes.apple.com/search?term=${enc(clean)}&entity=musicArtist&limit=10")
        val artistId = pickArtistId(data, clean)
        if (artistId != null) {
            val lookup = getJson("https://itunes.apple.com/lookup?id=$artistId&entity=album&limit=5")
            val results = lookup?.optJSONArray("results")
            if (results != null) {
                for (i in 0 until results.length()) {
                    val art = results.getJSONObject(i).optString("artworkUrl100", "")
                    if (art.isNotEmpty()) {
                        url = hiRes(art)
                        break
                    }
                }
            }
        }
        synchronized(artistCache) { artistCache[clean] = url }
        url
    }

    /**
     * Desktop Download.search_itunes_multi(): cleaned query, entity=song,
     * title/artist/album/year/genre + 600px art URL and 100px thumb.
     */
    data class ItunesHit(
        val title: String,
        val artist: String,
        val album: String,
        val year: String,
        val genre: String,
        val artworkUrl: String,
        val artworkThumb: String
    )

    suspend fun searchSongs(title: String, artist: String, limit: Int = 5): List<ItunesHit> =
        withContext(Dispatchers.IO) {
            var clean = title
            if (" - " in clean) clean = clean.split(" - ").last()
            clean = Regex("\\([^)]*\\)").replace(clean, "")
            clean = Regex("\\[[^\\]]*\\]").replace(clean, "")
            clean = Regex(
                "(official music video|official video|official lyric video|official audio|lyric video|lyrics|audio|visualizer)",
                RegexOption.IGNORE_CASE
            ).replace(clean, "")
            clean = clean.split(Regex("\\s+")).joinToString(" ").trim()
            val query = if (artist.isNotBlank() && artist != "Unknown") "$clean $artist".trim() else clean
            if (query.isBlank()) return@withContext emptyList()
            val data = getJson(
                "https://itunes.apple.com/search?term=${enc(query)}&entity=song&limit=${limit.coerceIn(1, 10)}",
                userAgent = "Mozilla/5.0"
            ) ?: return@withContext emptyList()
            val results = data.optJSONArray("results") ?: return@withContext emptyList()
            val out = mutableListOf<ItunesHit>()
            for (i in 0 until results.length()) {
                val r = results.optJSONObject(i) ?: continue
                val art100 = r.optString("artworkUrl100", "")
                out.add(
                    ItunesHit(
                        title = r.optString("trackName", title),
                        artist = r.optString("artistName", artist),
                        album = r.optString("collectionName", ""),
                        year = r.optString("releaseDate", "").take(4),
                        genre = r.optString("primaryGenreName", ""),
                        artworkUrl = hiRes(art100),
                        artworkThumb = art100
                    )
                )
            }
            out
        }

    private fun pickAlbumArtwork(data: JSONObject?, album: String, artist: String?): String? {
        val results = data?.optJSONArray("results") ?: return null
        val targetAlbum = album.lowercase()
        val targetArtist = artist?.lowercase().orEmpty()
        if (targetArtist.isNotEmpty()) {
            for (i in 0 until results.length()) {
                val r = results.getJSONObject(i)
                if (r.optString("collectionName", "").trim().lowercase() == targetAlbum &&
                    r.optString("artistName", "").trim().lowercase() == targetArtist
                ) {
                    hiRes(r.optString("artworkUrl100", "")).takeIf { it.isNotEmpty() }?.let { return it }
                }
            }
        }
        for (i in 0 until results.length()) {
            val r = results.getJSONObject(i)
            if (r.optString("collectionName", "").trim().lowercase() == targetAlbum) {
                hiRes(r.optString("artworkUrl100", "")).takeIf { it.isNotEmpty() }?.let { return it }
            }
        }
        for (i in 0 until results.length()) {
            hiRes(results.getJSONObject(i).optString("artworkUrl100", ""))
                .takeIf { it.isNotEmpty() }?.let { return it }
        }
        return null
    }

    private fun pickArtistId(data: JSONObject?, artist: String): Long? {
        val results = data?.optJSONArray("results") ?: return null
        val target = artist.lowercase()
        for (i in 0 until results.length()) {
            val r = results.getJSONObject(i)
            if (r.optString("artistName", "").trim().lowercase() == target && r.has("artistId")) {
                return r.optLong("artistId")
            }
        }
        for (i in 0 until results.length()) {
            val r = results.getJSONObject(i)
            if (r.has("artistId")) return r.optLong("artistId")
        }
        return null
    }

    private fun hiRes(url100: String): String =
        if (url100.isNotEmpty()) url100.replace("100x100bb", "600x600bb") else ""

    /** Raw bytes for a (cover-art) URL; null on any failure. */
    suspend fun fetchBytes(url: String): ByteArray? = withContext(Dispatchers.IO) {
        var conn: HttpURLConnection? = null
        try {
            conn = (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "GET"
                setRequestProperty("User-Agent", "Mozilla/5.0")
                connectTimeout = 20000
                readTimeout = 20000
            }
            if (conn.responseCode != HttpURLConnection.HTTP_OK) return@withContext null
            conn.inputStream.use { it.readBytes() }
        } catch (_: Exception) {
            null
        } finally {
            conn?.disconnect()
        }
    }

    private fun getJson(url: String, userAgent: String = "MyMusicPlayer/1.0"): JSONObject? {
        var conn: HttpURLConnection? = null
        return try {
            conn = (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "GET"
                setRequestProperty("User-Agent", userAgent)
                connectTimeout = 20000
                readTimeout = 20000
            }
            if (conn.responseCode != HttpURLConnection.HTTP_OK) return null
            JSONObject(conn.inputStream.bufferedReader().use { it.readText() })
        } catch (e: Exception) {
            Log.w(TAG, "Apple API request failed: ${e.message}")
            null
        } finally {
            conn?.disconnect()
        }
    }

    private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")

    companion object {
        private const val TAG = "ArtworkRepository"
    }
}
