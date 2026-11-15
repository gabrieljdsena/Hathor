package com.musicplayer.android.ui.aidj

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import kotlinx.coroutines.isActive
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin

private const val RADIAL_BARS = 72
private const val LINE_BARS = 64

/**
 * Web VohitheVisualizer equivalent: a radial ring of bars around the portrait.
 * The web reads the real audio spectrum; Android's Visualizer needs
 * RECORD_AUDIO and is unreliable on emulators, so this is a deterministic
 * idle/speaking animation with the same geometry and palette.
 */
@Composable
fun VohitheVisualizer(
    active: Boolean,
    voice: Boolean,
    modifier: Modifier = Modifier,
    animate: Boolean = true
) {
    var phase by remember { mutableFloatStateOf(0f) }
    LaunchedEffect(active, voice, animate) {
        if (!animate || (!active && !voice)) return@LaunchedEffect
        while (isActive) {
            withFrameNanos { phase = ((it / 1_000_000_000.0) % 1000.0).toFloat() }
        }
    }

    Canvas(modifier = modifier) {
        val size = min(this.size.width, this.size.height)
        if (size < 4f) return@Canvas
        val cx = this.size.width / 2f
        val cy = this.size.height / 2f
        val base = size * 0.335f
        val max = size * 0.115f
        val accent = if (voice) Color(0xFFFB923C) else Color(0xFFF97316)

        drawCircle(
            color = Color.White.copy(alpha = 0.07f),
            radius = base,
            center = Offset(cx, cy),
            style = androidx.compose.ui.graphics.drawscope.Stroke(width = 1f)
        )
        drawCircle(
            color = Color.White.copy(alpha = 0.035f),
            radius = base + max + 10f,
            center = Offset(cx, cy),
            style = androidx.compose.ui.graphics.drawscope.Stroke(width = 1f)
        )

        for (i in 0 until RADIAL_BARS) {
            val idle = 0.025f + (if (active) 0.018f else 0.008f) *
                sin(phase * 2f + i * 0.55f).toFloat()
            val speak = if (voice) {
                0.10f + 0.52f * abs(sin(phase * 3.1f + i * 0.37f)) *
                    (0.62f + 0.38f * sin(phase * 7.3f + i * 0.19f).toFloat())
            } else {
                0f
            }
            val magnitude = maxOf(idle, speak).coerceIn(0f, 1f)
            val angle = (i.toFloat() / RADIAL_BARS) * (2f * PI.toFloat()) - PI.toFloat() / 2f
            val start = base + 5f
            val end = start + 4f + magnitude * max
            drawLine(
                color = accent.copy(alpha = 0.12f + magnitude * 0.72f),
                start = Offset(cx + cos(angle) * start, cy + sin(angle) * start),
                end = Offset(cx + cos(angle) * end, cy + sin(angle) * end),
                strokeWidth = if (i % 6 == 0) 2.2f else 1.4f,
                cap = StrokeCap.Round
            )
        }
    }
}

/** Web AudioWaveform equivalent for the Voice card. */
@Composable
fun AudioWaveform(
    active: Boolean,
    modifier: Modifier = Modifier,
    animate: Boolean = true
) {
    var phase by remember { mutableFloatStateOf(0f) }
    LaunchedEffect(active, animate) {
        if (!animate || !active) return@LaunchedEffect
        while (isActive) {
            withFrameNanos { phase = ((it / 1_000_000_000.0) % 1000.0).toFloat() }
        }
    }

    Canvas(modifier = modifier) {
        if (size.width < 4f || size.height < 4f) return@Canvas
        val gap = 2f
        val barWidth = maxOf(1f, (size.width - gap * (LINE_BARS - 1)) / LINE_BARS)
        val mid = size.height / 2f
        for (i in 0 until LINE_BARS) {
            val idle = 0.04f + (if (active) 0.02f else 0.01f) *
                sin(phase * 2.1f + i * 0.5f).toFloat()
            val voice = if (active) {
                0.08f + 0.55f * abs(sin(phase * 3.4f + i * 0.31f))
            } else {
                0f
            }
            val magnitude = maxOf(idle, voice).coerceIn(0f, 1f)
            val barHeight = 2f + magnitude * (size.height - 4f)
            val x = i * (barWidth + gap)
            drawLine(
                color = Color(0xFFF97316).copy(alpha = 0.12f + magnitude * 0.7f),
                start = Offset(x + barWidth / 2f, mid - barHeight / 2f),
                end = Offset(x + barWidth / 2f, mid + barHeight / 2f),
                strokeWidth = barWidth,
                cap = StrokeCap.Round
            )
        }
    }
}
