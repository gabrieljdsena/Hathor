package com.musicplayer.android.data.api

import org.json.JSONObject

data class DiscoverItem(
    val title: String,
    val artist: String,
    val album: String,
    val year: String,
    val genre: String,
    val artworkUrl: String,
    val source: String,
    val score: Double,
    val mbid: String?,
    val reason: String?,
    val seedArtist: String?
)

data class DiscoverFeed(
    val date: String,
    val items: List<DiscoverItem>,
    val cached: Boolean
)

/**
 * Discover is computed entirely by the Hathor Web API; the mobile client only
 * renders the feed and posts feedback. Downloads use the on-device YouTube
 * engine (no server-side ingest).
 */
object DiscoverApi {

    suspend fun load(): DiscoverFeed =
        parse(HathorApi().getJson("/discover"))

    suspend fun refresh(): DiscoverFeed =
        parse(HathorApi().postJson("/discover/refresh") ?: JSONObject())

    suspend fun feedback(item: DiscoverItem, event: String) {
        val body = JSONObject()
            .put("Title", item.title)
            .put("Artist", item.artist)
            .put("Mbid", item.mbid)
            .put("Source", item.source)
            .put("Event", event)
            .put("SeedArtist", item.seedArtist)
            .put("Score", item.score)
        HathorApi().postNoContent("/discover/feedback", body)
    }

    internal fun parse(o: JSONObject): DiscoverFeed {
        val arr = o.optJSONArray("items")
        val items = buildList {
            if (arr != null) {
                for (i in 0 until arr.length()) {
                    val it = arr.optJSONObject(i) ?: continue
                    add(
                        DiscoverItem(
                            title = it.optString("title"),
                            artist = it.optString("artist"),
                            album = it.optString("album"),
                            year = it.optString("year"),
                            genre = it.optString("genre"),
                            artworkUrl = it.optString("artworkUrl"),
                            source = it.optString("source", "chart"),
                            score = it.optDouble("score", 0.0),
                            mbid = it.optString("mbid").takeIf { s -> s.isNotBlank() },
                            reason = it.optString("reason").takeIf { s -> s.isNotBlank() },
                            seedArtist = it.optString("seedArtist").takeIf { s -> s.isNotBlank() }
                        )
                    )
                }
            }
        }
        return DiscoverFeed(
            date = o.optString("date"),
            items = items,
            cached = o.optBoolean("cached", false)
        )
    }
}
