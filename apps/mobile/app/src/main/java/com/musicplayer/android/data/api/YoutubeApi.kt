package com.musicplayer.android.data.api

import org.json.JSONArray
import org.json.JSONObject

data class YouTubeHit(
    val id: String,
    val title: String,
    val uploader: String,
    val durationSec: Long,
    val thumbnail: String
) {
    val pageUrl: String get() = "https://www.youtube.com/watch?v=$id"
}

data class YouTubePreview(
    val id: String,
    val embeddable: Boolean,
    val audioUrl: String?
)

/**
 * Web-parity YouTube lookup for Discover previews/download candidates.
 * The server's yt-dlp has the cookie/POT protection setup the phone lacks.
 */
object YoutubeApi {

    suspend fun search(query: String, limit: Int = 5): List<YouTubeHit> {
        val arr = HathorApi().getArray(
            "/youtube/search",
            mapOf("q" to query, "limit" to limit.coerceIn(1, 25).toString())
        )
        return parseSearch(arr)
    }

    suspend fun preview(videoId: String): YouTubePreview {
        val o = HathorApi().getJson(
            "/youtube/preview",
            mapOf("id" to videoId, "resolveAudio" to "true")
        )
        return parsePreview(o)
    }

    internal fun parseSearch(arr: JSONArray): List<YouTubeHit> = buildList {
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: continue
            val id = o.textOrNull("id") ?: continue
            add(
                YouTubeHit(
                    id = id,
                    title = o.textOrNull("title").orEmpty(),
                    uploader = o.textOrNull("uploader").orEmpty(),
                    durationSec = o.optDouble("durationSec", 0.0).toLong(),
                    thumbnail = o.textOrNull("thumbnail").orEmpty()
                )
            )
        }
    }

    internal fun parsePreview(o: JSONObject): YouTubePreview = YouTubePreview(
        id = o.textOrNull("id").orEmpty(),
        embeddable = o.optBoolean("embeddable", false),
        audioUrl = o.textOrNull("audioUrl")?.takeIf { it.startsWith("http") }
    )

    private fun JSONObject.textOrNull(name: String): String? {
        if (!has(name) || isNull(name)) return null
        val value = optString(name).trim()
        return value.takeIf { it.isNotEmpty() && !it.equals("null", ignoreCase = true) }
    }
}
