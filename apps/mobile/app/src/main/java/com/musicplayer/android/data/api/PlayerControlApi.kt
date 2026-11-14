package com.musicplayer.android.data.api

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/**
 * Web-equivalent player controls used by the AI DJ session. The Web API keeps
 * the queue and playback source; the phone still plays its synced local files.
 * This is what lets the server prefetcher append future DJ batches instead of
 * leaving the session stuck on batch 1.
 */
object PlayerControlApi {

    suspend fun setDjQueue(
        currentFile: String,
        files: List<String>,
        sessionId: String
    ) {
        val body = JSONObject()
            .put("CurrentFile", currentFile)
            .put("Files", JSONArray(files))
            .put(
                "Source",
                JSONObject()
                    .put("Type", "ai_dj")
                    .put("Id", sessionId)
            )
        HathorApi().putJson("/player/queue", body)
    }

    suspend fun play(file: String) {
        val body = JSONObject()
            .put("File", file)
            .put("InstanceId", UUID.randomUUID().toString())
        HathorApi().postJson("/player/play", body)
    }

    /** Advance the server-side queue so batch rotation and prefetch keep up. */
    suspend fun next(auto: Boolean, expectedFile: String?) {
        val body = JSONObject().put("Auto", auto)
        if (!expectedFile.isNullOrBlank()) body.put("ExpectedFile", expectedFile)
        HathorApi().postJson("/player/next", body)
    }
}
