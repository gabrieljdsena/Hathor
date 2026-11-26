package com.musicplayer.android.ui

import android.graphics.Bitmap
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorMotion

/**
 * Shared artwork surface: consistent crop/radius/placeholder with a short
 * crossfade when the cover changes. Callers provide a fixed size in [modifier].
 */
@Composable
fun HathorArtwork(
    bitmap: Bitmap?,
    contentDescription: String?,
    modifier: Modifier = Modifier,
    cornerRadius: Dp = 12.dp,
    placeholderIcon: ImageVector = Icons.Filled.MusicNote
) {
    Crossfade(
        targetState = bitmap,
        animationSpec = tween(durationMillis = 220, easing = HathorMotion.Easing),
        label = "artwork"
    ) { current ->
        Box(
            modifier = modifier
                .clip(RoundedCornerShape(cornerRadius))
                .background(HathorColors.AccentFill),
            contentAlignment = Alignment.Center
        ) {
            if (current != null) {
                Image(
                    bitmap = current.asImageBitmap(),
                    contentDescription = contentDescription,
                    modifier = Modifier.fillMaxSize(),
                    contentScale = ContentScale.Crop
                )
            } else {
                Icon(
                    placeholderIcon,
                    contentDescription = null,
                    tint = HathorColors.AccentBright,
                    modifier = Modifier.fillMaxSize(0.5f)
                )
            }
        }
    }
}
