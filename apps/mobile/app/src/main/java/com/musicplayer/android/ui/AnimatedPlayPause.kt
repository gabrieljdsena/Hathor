package com.musicplayer.android.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.togetherWith
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color

/** Shared play/pause glyph with a short scale/fade morph. */
@Composable
fun AnimatedPlayPauseIcon(
    playing: Boolean,
    modifier: Modifier = Modifier,
    tint: Color = Color.White
) {
    AnimatedContent(
        targetState = playing,
        transitionSpec = {
            (fadeIn(animationSpec = tween(140)) +
                scaleIn(animationSpec = tween(140), initialScale = 0.8f)) togetherWith
                (fadeOut(animationSpec = tween(100)) +
                    scaleOut(animationSpec = tween(100), targetScale = 0.8f))
        },
        label = "play-pause"
    ) { isPlaying ->
        Icon(
            if (isPlaying) Icons.Filled.Pause else Icons.Filled.PlayArrow,
            contentDescription = if (isPlaying) "Pause" else "Play",
            tint = tint,
            modifier = modifier
        )
    }
}
