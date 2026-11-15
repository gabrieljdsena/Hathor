package com.musicplayer.android.data.api

import org.json.JSONArray
import org.json.JSONObject

data class AiDjTrack(
    val file: String,
    val title: String,
    val artist: String,
    val album: String,
    val durationSec: Int
)

data class AiDjSettings(
    val batchSize: Int,
    val commentaryMode: String,
    val language: String,
    val discoveryLevel: Double,
    val brief: String,
    val preferredGenres: String,
    val avoidArtists: String,
    val duckLevel: Double,
    val voiceLeadSeconds: Int
)

data class AiDjBatch(
    val id: String,
    val number: Int,
    val status: String,
    val tracks: List<AiDjTrack>,
    val introText: String?,
    val introClipIds: List<String>,
    val introDurationSec: Double,
    val outroText: String?,
    val outroClipIds: List<String>,
    val outroDurationSec: Double,
    val shouldComment: Boolean,
    val mood: String?,
    val reason: String?
)

data class AiDjState(
    val enabled: Boolean,
    val configured: Boolean,
    val sessionId: String?,
    val status: String,
    val settings: AiDjSettings?,
    val currentBatch: AiDjBatch?,
    val nextBatch: AiDjBatch?,
    val error: String?
)

/**
 * AI DJ (Vohithe) is a Web API feature: track selection, batches, commentary
 * and voice clips all live on the server. Mobile only starts/stops sessions,
 * plays the returned local files and reports feedback.
 */
object AiDjApi {

    suspend fun state(): AiDjState =
        parse(HathorApi().getJson("/ai-dj/state"))

    suspend fun start(brief: String): AiDjState {
        val body = JSONObject().put("Brief", brief.trim().takeIf { it.isNotEmpty() })
        val json = HathorApi().postJson("/ai-dj/start", body)
            ?: throw HathorApiException("AI DJ returned an empty response.")
        return parse(json)
    }

    suspend fun stop(): AiDjState {
        val json = HathorApi().postJson("/ai-dj/stop")
            ?: throw HathorApiException("AI DJ returned an empty response.")
        return parse(json)
    }

    suspend fun feedback(
        sessionId: String?,
        batchId: String?,
        trackFile: String?,
        event: String
    ) {
        val body = JSONObject()
            .put("SessionId", sessionId)
            .put("BatchId", batchId)
            .put("TrackFile", trackFile)
            .put("Event", event)
        HathorApi().postNoContent("/ai-dj/feedback", body)
    }

    /** Spoken vibe input: server-side transcription of a short WAV clip. */
    suspend fun transcribe(audioBase64: String, language: String?): String {
        val body = JSONObject()
            .put("Audio", audioBase64)
            .put("Language", language?.takeIf { it.isNotBlank() })
        val json = HathorApi().postJson("/ai-dj/transcribe", body)
            ?: throw HathorApiException("Transcription returned an empty response.")
        return json.optString("text").trim()
    }

    internal fun parse(o: JSONObject): AiDjState {
        val settingsObj = o.optJSONObject("settings")
        val settings = settingsObj?.let {
            AiDjSettings(
                batchSize = it.optInt("batchSize", 3),
                commentaryMode = it.optString("commentaryMode", "auto"),
                language = it.optString("language", "auto"),
                discoveryLevel = it.optDouble("discoveryLevel", 0.35),
                brief = it.optString("brief"),
                preferredGenres = it.optString("preferredGenres"),
                avoidArtists = it.optString("avoidArtists"),
                duckLevel = it.optDouble("duckLevel", 0.18).coerceIn(0.02, 0.8),
                voiceLeadSeconds = it.optInt("voiceLeadSeconds", 10).coerceIn(1, 30)
            )
        }
        return AiDjState(
            enabled = o.optBoolean("enabled", false),
            configured = o.optBoolean("configured", false),
            sessionId = o.textOrNull("sessionId"),
            status = o.optString("status", "idle"),
            settings = settings,
            currentBatch = parseBatch(o.optJSONObject("currentBatch")),
            nextBatch = parseBatch(o.optJSONObject("nextBatch")),
            error = o.textOrNull("error")
        )
    }

    /** org.json turns JSON null into the string "null"; optional fields need this. */
    private fun JSONObject.textOrNull(name: String): String? {
        if (!has(name) || isNull(name)) return null
        val value = optString(name).trim()
        return value.takeIf { it.isNotEmpty() && !it.equals("null", ignoreCase = true) }
    }

    private fun parseBatch(o: JSONObject?): AiDjBatch? {
        if (o == null) return null
        val tracks = mutableListOf<AiDjTrack>()
        o.optJSONArray("tracks")?.let { arr ->
            for (i in 0 until arr.length()) {
                val t = arr.optJSONObject(i) ?: continue
                tracks.add(
                    AiDjTrack(
                        file = t.optString("file"),
                        title = t.textOrNull("title").orEmpty(),
                        artist = t.textOrNull("artist").orEmpty(),
                        album = t.textOrNull("album").orEmpty(),
                        durationSec = t.optDouble("duration", 0.0).toInt()
                    )
                )
            }
        }
        fun clipIds(name: String): List<String> {
            val arr: JSONArray = o.optJSONArray(name) ?: return emptyList()
            return buildList {
                for (i in 0 until arr.length()) {
                    val id = arr.optString(i)
                    if (id.isNotBlank() && !id.equals("null", ignoreCase = true)) add(id)
                }
            }
        }
        return AiDjBatch(
            id = o.optString("id"),
            number = o.optInt("number", 0),
            status = o.optString("status"),
            tracks = tracks,
            introText = o.textOrNull("introText"),
            introClipIds = clipIds("introClipIds"),
            introDurationSec = o.optDouble("introDurationSec", 0.0),
            outroText = o.textOrNull("outroText"),
            outroClipIds = clipIds("outroClipIds"),
            outroDurationSec = o.optDouble("outroDurationSec", 0.0),
            shouldComment = o.optBoolean("shouldComment", false),
            mood = o.textOrNull("mood"),
            reason = o.textOrNull("reason")
        )
    }
}
