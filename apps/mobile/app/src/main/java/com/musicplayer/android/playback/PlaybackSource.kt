package com.musicplayer.android.playback

import com.musicplayer.android.ui.shell.Dest
import org.json.JSONObject

/**
 * Playback context port (desktop playback.py VALID_SOURCE_TYPES +
 * index.html _sourceForCurrentView).
 *
 * Persisted backend-side (Settings.queue_source JSON) so a restart rebuilds
 * the queue from the right place. Null = playlist/general fallback logic.
 * Types: playlist, daily_mix, all_songs, artist, album, recently_played,
 * recently_downloaded, podcast.
 */
data class PlaybackSource(val type: String, val id: String? = null) {
    fun toJson(): String = JSONObject()
        .put("type", type)
        .put("id", id)
        .toString()

    companion object {
        val VALID = setOf(
            "playlist", "daily_mix", "all_songs",
            "artist", "album", "recently_played", "recently_downloaded",
            "podcast"
        )

        fun fromJson(raw: String?): PlaybackSource? {
            if (raw.isNullOrBlank()) return null
            return try {
                val o = JSONObject(raw)
                val t = o.optString("type", "")
                if (t !in VALID) return null
                val id = o.optString("id", "").ifBlank { null }
                // JSONObject stores null as "null" string via put(null)? Guard:
                val fixedId = if (o.isNull("id")) null else id
                PlaybackSource(t, fixedId)
            } catch (_: Exception) {
                null
            }
        }

        /**
         * Resolve the playback context for the active view (desktop
         * _sourceForCurrentView): all_songs / daily_mix+date / artist+name /
         * album+name; null otherwise (playlist/general fallback owns it).
         */
        fun forView(
            dest: Dest,
            dailyDate: String? = null,
            artistName: String? = null,
            albumName: String? = null
        ): PlaybackSource? = when (dest) {
            Dest.AllSongs -> PlaybackSource("all_songs", null)
            Dest.DailyMix -> PlaybackSource("daily_mix", dailyDate)
            Dest.Library -> null // dashboard strips pass their own strip source
            else -> null
        }.let { base ->
            // Artist/album detail screens sit inside AllSongs; when open they
            // override the list context (desktop currentArtistName/AlbumName).
            when {
                !artistName.isNullOrBlank() -> PlaybackSource("artist", artistName.trim())
                !albumName.isNullOrBlank() -> PlaybackSource("album", albumName.trim())
                else -> base
            }
        }
    }
}
