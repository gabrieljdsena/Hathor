package com.musicplayer.android.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musicplayer.android.ui.theme.HathorColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL

/**
 * Minimal URL image loader (memory LRU only). No new dependency: the app only
 * needs remote thumbs (iTunes artwork), and a full image pipeline would be
 * overkill next to the retriever/decoder paths already in use.
 */
object ImageLoader {
    private const val TAG = "ImageLoader"
    // Bitmap cache bounded by BYTES (24 MB shared), keyed url@width so a 40dp
    // row never pays for (or holds) a 600px decode.
    private val cache = object : LruCache<String, Bitmap>(24 * 1024) {
        override fun sizeOf(key: String, value: Bitmap): Int = value.byteCount / 1024
    }

    suspend fun get(url: String, reqWidthPx: Int = 0): Bitmap? = withContext(Dispatchers.IO) {
        val key = if (reqWidthPx > 0) "$url@$reqWidthPx" else url
        synchronized(cache) { cache.get(key) }?.let { return@withContext it }
        var conn: HttpURLConnection? = null
        try {
            conn = (URL(url).openConnection() as HttpURLConnection).apply {
                requestMethod = "GET"
                setRequestProperty("User-Agent", "MyMusicPlayer/1.0")
                connectTimeout = 15000
                readTimeout = 15000
            }
            if (conn.responseCode != HttpURLConnection.HTTP_OK) return@withContext null
            val bytes = conn.inputStream.use { it.readBytes() }
            val bmp = decodeSampled(bytes, reqWidthPx) ?: return@withContext null
            synchronized(cache) { cache.put(key, bmp) }
            bmp
        } catch (e: Exception) {
            Log.w(TAG, "image fetch failed: ${e.message}")
            null
        } finally {
            conn?.disconnect()
        }
    }

    /**
     * Decode bounds-first and downsample (power-of-2 inSampleSize) so a 56dp
     * thumb never materializes a 3000px bitmap. reqWidthPx<=0 = full decode.
     */
    fun decodeSampled(bytes: ByteArray, reqWidthPx: Int): Bitmap? {
        return try {
            if (reqWidthPx <= 0) {
                return BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            }
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            if (bounds.outWidth <= 0) {
                return BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            }
            var sample = 1
            while (bounds.outWidth / (sample * 2) >= reqWidthPx) sample *= 2
            val opts = BitmapFactory.Options().apply { inSampleSize = sample }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)
        } catch (_: Exception) {
            null
        }
    }
}

@Composable
fun UrlThumb(url: String?, size: Dp = 56.dp) {
    var bitmap by remember(url) { mutableStateOf<Bitmap?>(null) }
    val density = androidx.compose.ui.platform.LocalDensity.current
    LaunchedEffect(url) {
        bitmap = if (url.isNullOrBlank()) null else {
            val px = with(density) { size.toPx() }.toInt() * 2
            ImageLoader.get(url, px)
        }
    }
    Box(modifier = Modifier.size(size), contentAlignment = Alignment.Center) {
        val bmp = bitmap
        if (bmp != null) {
            Image(bitmap = bmp.asImageBitmap(), contentDescription = null, modifier = Modifier.size(size))
        } else {
            Icon(Icons.Filled.MusicNote, contentDescription = null, tint = HathorColors.AccentBright, modifier = Modifier.size(size.times(0.6f)))
        }
    }
}
