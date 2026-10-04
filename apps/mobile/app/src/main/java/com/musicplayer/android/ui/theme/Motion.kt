package com.musicplayer.android.ui.theme

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.Orientation
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.draggable
import androidx.compose.foundation.gestures.rememberDraggableState
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.composed
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween

/**
 * Tailwind-motion port: the desktop animates nearly everything with
 * `transition-all duration-300 ease-[cubic-bezier(0.4,0,0.2,1)]` and
 * `active:scale-95` on pressables. FastOutSlowInEasing IS that cubic.
 */
object HathorMotion {
    const val DURATION_MS = 300
    val Easing = FastOutSlowInEasing
    val EasingBezier = CubicBezierEasing(0.4f, 0.0f, 0.2f, 1.0f)

    fun <T> tween300(): androidx.compose.animation.core.FiniteAnimationSpec<T> =
        tween(durationMillis = DURATION_MS, easing = Easing)
}

/**
 * Desktop `active:scale-95 transition-all`: pressable scales to 0.95 while
 * held, springing back on release. Use on rows/cards instead of bare clickable.
 */
fun Modifier.hathorPressable(onClick: () -> Unit): Modifier = composed {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.95f else 1f,
        animationSpec = tween(durationMillis = 150, easing = HathorMotion.Easing),
        label = "press"
    )
    this
        .scale(scale)
        .clickable(interactionSource = interaction, indication = null, onClick = onClick)
}

/**
 * Modern thin slider (4dp rounded track, 14dp thumb) replacing the chunky
 * default: tap-to-seek plus horizontal drag, same accent styling as the bar.
 */
@Composable
fun ModernSlider(
    value: Float,
    onValueChange: (Float) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    onValueChangeFinished: (() -> Unit)? = null
) {
    var dragValue by remember { mutableStateOf<Float?>(null) }
    val shown = (dragValue ?: value).coerceIn(0f, 1f)
    val barColor = if (enabled) HathorColors.Accent else HathorColors.TextHint.copy(alpha = 0.4f)
    val density = androidx.compose.ui.platform.LocalDensity.current
    BoxWithConstraints(modifier = modifier.height(28.dp)) {
        val widthPx = constraints.maxWidth.toFloat().coerceAtLeast(1f)
        val halfThumbPx = with(density) { 7.dp.toPx() }.toInt()
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(28.dp)
                .pointerInput(enabled, widthPx) {
                    detectTapGestures { offset ->
                        val v = (offset.x / widthPx).coerceIn(0f, 1f)
                        dragValue = null
                        if (enabled) {
                            onValueChange(v)
                            onValueChangeFinished?.invoke()
                        }
                    }
                }
                .draggable(
                    state = rememberDraggableState { delta ->
                        val cur = dragValue ?: value
                        val v = ((cur * widthPx) + delta) / widthPx
                        val clamped = v.coerceIn(0f, 1f)
                        dragValue = clamped
                        onValueChange(clamped)
                    },
                    orientation = Orientation.Horizontal,
                    enabled = enabled,
                    onDragStopped = {
                        dragValue = null
                        onValueChangeFinished?.invoke()
                    }
                )
        ) {
            Box(
                modifier = Modifier
                    .align(Alignment.CenterStart)
                    .fillMaxWidth()
                    .height(4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(HathorColors.TextHint.copy(alpha = 0.3f))
            )
            Box(
                modifier = Modifier
                    .align(Alignment.CenterStart)
                    .fillMaxWidth(shown)
                    .height(4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(barColor)
            )
            Box(
                modifier = Modifier
                    .align(Alignment.CenterStart)
                    .offset {
                        val px = (shown * widthPx).toInt()
                        IntOffset(px - halfThumbPx, 0)
                    }
                    .size(14.dp)
                    .clip(CircleShape)
                    .background(barColor)
            )
        }
    }
}
