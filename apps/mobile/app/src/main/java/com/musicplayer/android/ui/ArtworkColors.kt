package com.musicplayer.android.ui

import android.graphics.Bitmap
import android.graphics.Color as AndroidColor
import androidx.compose.runtime.Composable
import androidx.compose.runtime.produceState
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import com.musicplayer.android.ui.theme.HathorColors
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Artwork-derived accent for ambient player backdrops. Samples a small grid,
 * weights colorful mid-tones, then keeps the result close to the brand orange.
 * Cheap enough for on-device use and avoids expensive full-screen blur.
 */
@Composable
fun rememberArtworkAccent(bitmap: Bitmap?): Color {
    val state = produceState(initialValue = HathorColors.Accent, bitmap) {
        value = bitmap?.let { computeArtworkAccent(it) } ?: HathorColors.Accent
    }
    return state.value
}

private suspend fun computeArtworkAccent(bitmap: Bitmap): Color = withContext(Dispatchers.Default) {
    val stepX = (bitmap.width / 24).coerceAtLeast(1)
    val stepY = (bitmap.height / 24).coerceAtLeast(1)
    var red = 0.0
    var green = 0.0
    var blue = 0.0
    var totalWeight = 0.0
    var y = 0
    while (y < bitmap.height) {
        var x = 0
        while (x < bitmap.width) {
            val pixel = bitmap.getPixel(x, y)
            if (AndroidColor.alpha(pixel) >= 32) {
                val r = AndroidColor.red(pixel)
                val g = AndroidColor.green(pixel)
                val b = AndroidColor.blue(pixel)
                val max = maxOf(r, g, b)
                val min = minOf(r, g, b)
                val saturation = if (max == 0) 0f else (max - min) / max.toFloat()
                val luminance = (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f
                // Favor colorful mid-tones so black/white artwork does not win.
                val weight = (0.25f + saturation) *
                    (1f - abs(luminance - 0.5f)).coerceAtLeast(0.2f)
                red += r * weight
                green += g * weight
                blue += b * weight
                totalWeight += weight
            }
            x += stepX
        }
        y += stepY
    }
    if (totalWeight <= 0.0) return@withContext HathorColors.Accent

    val hsv = FloatArray(3)
    AndroidColor.RGBToHSV(
        (red / totalWeight).roundToInt().coerceIn(0, 255),
        (green / totalWeight).roundToInt().coerceIn(0, 255),
        (blue / totalWeight).roundToInt().coerceIn(0, 255),
        hsv
    )
    if (hsv[1] < 0.12f) return@withContext HathorColors.Accent

    hsv[1] = hsv[1].coerceIn(0.45f, 0.85f)
    hsv[2] = hsv[2].coerceIn(0.55f, 0.9f)
    val sampled = Color(AndroidColor.HSVToColor(floatArrayOf(hsv[0], hsv[1], hsv[2])))
    lerp(sampled, HathorColors.Accent, 0.2f)
}
