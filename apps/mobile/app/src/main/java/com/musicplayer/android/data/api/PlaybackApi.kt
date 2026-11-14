package com.musicplayer.android.data.api

import org.json.JSONArray
import org.json.JSONObject

data class PlaybackSpot(
    val device: String?,
    val file: String,
    val positionSec: Double,
    val isPodcast: Boolean,
    val updatedUtc: String?,
    val queueFiles: List<String>,
    val queueSourceType: String?,
    val queueSourceId: String?
)

/**
 * Cross-device resume spots via the Web API (`/api/v1/sync/playback`).
 * The Web API stores one spot per device; mobile pushes on pause and reads
 * another device's spot on Home.
 */
object PlaybackApi {

    suspend fun push(
        deviceId: String,
        deviceLabel: String,
        file: String,
        positionSec: Double,
        isPodcast: Boolean,
        queueFiles: List<String>,
        sourceType: String?,
        sourceId: String?
    ) {
        val body = JSONObject()
            .put("DeviceId", deviceId)
            .put("DeviceLabel", deviceLabel)
            .put("File", file)
            .put("PositionSec", positionSec)
            .put("IsPodcast", isPodcast)
            .put("QueueFiles", JSONArray(queueFiles))
        if (sourceType != null) {
            body.put(
                "QueueSource",
                JSONObject().put("Type", sourceType).put("Id", sourceId)
            )
        }
        HathorApi().postNoContent("/sync/playback", body)
    }

    suspend fun latest(excludeDevice: String): PlaybackSpot? {
        val text = HathorApi().getText(
            "/sync/playback",
            if (excludeDevice.isBlank()) emptyMap() else mapOf("excludeDevice" to excludeDevice)
        ).trim()
        return parseSpot(text)
    }

    internal fun parseSpot(text: String): PlaybackSpot? {
        if (text.isBlank() || text == "null") return null
        val o = JSONObject(text)
        val queue = o.optJSONArray("queueFiles")?.let { arr ->
            buildList {
                for (i in 0 until arr.length()) {
                    val f = arr.optString(i)
                    if (f.isNotBlank()) add(f)
                }
            }
        } ?: emptyList()
        val source = o.optJSONObject("queueSource")
        return PlaybackSpot(
            device = o.optString("device").takeIf { it.isNotBlank() },
            file = o.optString("file"),
            positionSec = o.optDouble("positionSec", 0.0),
            isPodcast = o.optBoolean("isPodcast", false),
            updatedUtc = o.optString("updatedUtc").takeIf { it.isNotBlank() },
            queueFiles = queue,
            queueSourceType = source?.optString("type")?.takeIf { it.isNotBlank() },
            queueSourceId = source?.optString("id")?.takeIf { it.isNotBlank() }
        )
    }
}
