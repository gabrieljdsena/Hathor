package com.musicplayer.android.playback

import android.content.Context
import android.media.MediaPlayer
import android.util.Log
import com.musicplayer.android.data.api.HathorApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import kotlin.coroutines.resume

/**
 * Plays Vohithe's server-generated commentary clips over the music bus.
 *
 * Clips are downloaded once into cacheDir and then played sequentially with a
 * dedicated MediaPlayer, so playback survives API hiccups and does not depend
 * on the clip endpoint supporting streaming on every Android build.
 */
class DjVoicePlayer(private val context: Context) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val cacheDir = File(context.cacheDir, "ai-dj-voice")
    private var job: Job? = null
    private var current: MediaPlayer? = null
    private var generation = 0

    private val _speaking = MutableStateFlow(false)
    val speaking: StateFlow<Boolean> = _speaking.asStateFlow()

    /**
     * Play [clipIds] in order. [onDone] runs only when the whole set finished
     * naturally; stop()/replacement cancels it.
     */
    fun play(clipIds: List<String>, onDone: (() -> Unit)? = null) {
        stop()
        if (clipIds.isEmpty()) {
            onDone?.invoke()
            return
        }
        val gen = generation
        job = scope.launch {
            _speaking.value = true
            try {
                for (id in clipIds) {
                    if (gen != generation) return@launch
                    val file = ensureClip(id) ?: continue
                    if (gen != generation) return@launch
                    playOne(file)
                }
            } catch (e: Exception) {
                Log.w(TAG, "voice playback failed: ${e.message}")
            } finally {
                if (gen == generation) {
                    _speaking.value = false
                    onDone?.invoke()
                }
            }
        }
    }

    fun stop() {
        generation++
        job?.cancel()
        job = null
        releaseCurrent()
        _speaking.value = false
    }

    private suspend fun ensureClip(id: String): File? = withContext(Dispatchers.IO) {
        try {
            if (!cacheDir.exists()) cacheDir.mkdirs()
            val safe = id.replace(Regex("[^A-Za-z0-9-]"), "")
            if (safe.isBlank()) return@withContext null
            val target = File(cacheDir, "$safe.mp3")
            if (target.length() > 0) return@withContext target
            val part = File(cacheDir, "$safe.part")
            val conn = URL(HathorApi().clipUrl(id)).openConnection() as HttpURLConnection
            try {
                conn.requestMethod = "GET"
                conn.connectTimeout = 10_000
                conn.readTimeout = 20_000
                conn.setRequestProperty("Accept", "audio/*")
                val code = conn.responseCode
                if (code !in 200..299) {
                    Log.w(TAG, "clip $id returned HTTP $code")
                    return@withContext null
                }
                conn.inputStream.use { input ->
                    part.outputStream().use { output -> input.copyTo(output) }
                }
                if (part.length() <= 0L) {
                    part.delete()
                    return@withContext null
                }
                if (!part.renameTo(target)) {
                    part.copyTo(target, overwrite = true)
                    part.delete()
                }
                target
            } finally {
                conn.disconnect()
            }
        } catch (e: Exception) {
            Log.w(TAG, "clip $id download failed: ${e.message}")
            null
        }
    }

    private suspend fun playOne(file: File): Boolean =
        withContext(Dispatchers.Main.immediate) {
            suspendCancellableCoroutine { cont ->
                var settled = false
                val mp = MediaPlayer()
                fun finish(ok: Boolean) {
                    if (settled) return
                    settled = true
                    if (current === mp) current = null
                    try {
                        mp.release()
                    } catch (_: Exception) {
                    }
                    if (cont.isActive) cont.resume(ok)
                }
                try {
                    mp.setDataSource(file.absolutePath)
                    mp.prepare()
                    mp.setOnCompletionListener { finish(true) }
                    mp.setOnErrorListener { _, _, _ ->
                        finish(false)
                        true
                    }
                    cont.invokeOnCancellation {
                        try {
                            mp.stop()
                        } catch (_: Exception) {
                        }
                        finish(false)
                    }
                    current = mp
                    mp.start()
                } catch (e: Exception) {
                    Log.w(TAG, "clip play failed: ${e.message}")
                    finish(false)
                }
            }
        }

    private fun releaseCurrent() {
        val mp = current
        current = null
        if (mp == null) return
        try {
            mp.stop()
        } catch (_: Exception) {
        }
        try {
            mp.release()
        } catch (_: Exception) {
        }
    }

    companion object {
        private const val TAG = "DjVoice"
    }
}
