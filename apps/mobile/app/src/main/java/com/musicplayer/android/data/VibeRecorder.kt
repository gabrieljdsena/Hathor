package com.musicplayer.android.data

import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Short spoken vibe capture for the AI DJ. Records 16 kHz mono PCM (WAV on
 * stop) and leaves transcription to the Web API's /ai-dj/transcribe endpoint.
 */
class VibeRecorder {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val pcm = ByteArrayOutputStream()
    private var record: AudioRecord? = null
    private var readJob: Job? = null
    private var totalBytes = 0

    private val _recording = MutableStateFlow(false)
    val recording: StateFlow<Boolean> = _recording.asStateFlow()

    fun start() {
        if (_recording.value) return
        try {
            record?.release()
        } catch (_: Exception) {
        }
        record = null
        val min = AudioRecord.getMinBufferSize(
            SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (min <= 0) throw IllegalStateException("Microphone is not available.")
        val bufferSize = maxOf(min, 4096)
        val rec = AudioRecord(
            MediaRecorder.AudioSource.MIC,
            SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            bufferSize * 2
        )
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            throw IllegalStateException("Could not open the microphone.")
        }
        synchronized(pcm) { pcm.reset() }
        totalBytes = 0
        record = rec
        rec.startRecording()
        _recording.value = true
        Log.i(TAG, "start: state=${rec.state} buffer=$bufferSize")
        readJob = scope.launch {
            val buffer = ByteArray(bufferSize)
            try {
                while (isActive && _recording.value) {
                    val n = rec.read(buffer, 0, buffer.size)
                    if (n > 0) {
                        synchronized(pcm) { pcm.write(buffer, 0, n) }
                        totalBytes += n
                        if (totalBytes >= SAMPLE_RATE * 2 * MAX_SECONDS) {
                            _recording.value = false
                            break
                        }
                    } else if (n < 0) {
                        Log.w(TAG, "AudioRecord.read error $n")
                    }
                }
            } finally {
                try {
                    rec.stop()
                } catch (_: Exception) {
                }
            }
        }
    }

    /** Stop and return a complete WAV file (empty when nothing was captured). */
    suspend fun stop(): ByteArray = withContext(Dispatchers.IO) {
        _recording.value = false
        val rec = record
        record = null
        try {
            rec?.stop()
        } catch (_: Exception) {
        }
        readJob?.join()
        readJob = null
        try {
            rec?.release()
        } catch (_: Exception) {
        }
        val pcmBytes = synchronized(pcm) { pcm.toByteArray() }
        Log.i(TAG, "stop: pcm=${pcmBytes.size}")
        if (pcmBytes.isEmpty()) ByteArray(0) else wav(pcmBytes)
    }

    fun cancel() {
        _recording.value = false
        val rec = record
        record = null
        readJob?.cancel()
        readJob = null
        try {
            rec?.stop()
        } catch (_: Exception) {
        }
        try {
            rec?.release()
        } catch (_: Exception) {
        }
        synchronized(pcm) { pcm.reset() }
    }

    private fun wav(pcmBytes: ByteArray): ByteArray {
        val out = ByteArrayOutputStream(44 + pcmBytes.size)
        val dataSize = pcmBytes.size
        val byteRate = SAMPLE_RATE * 2
        fun le32(v: Int) {
            out.write(
                ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(v).array()
            )
        }
        fun le16(v: Int) {
            out.write(
                ByteBuffer.allocate(2).order(ByteOrder.LITTLE_ENDIAN).putShort(v.toShort())
                    .array()
            )
        }
        out.write("RIFF".toByteArray())
        le32(36 + dataSize)
        out.write("WAVE".toByteArray())
        out.write("fmt ".toByteArray())
        le32(16)
        le16(1) // PCM
        le16(1) // mono
        le32(SAMPLE_RATE)
        le32(byteRate)
        le16(2) // block align
        le16(16) // bits per sample
        out.write("data".toByteArray())
        le32(dataSize)
        out.write(pcmBytes)
        return out.toByteArray()
    }

    companion object {
        private const val TAG = "VibeRecorder"
        const val SAMPLE_RATE = 16_000
        const val MAX_SECONDS = 30
    }
}
