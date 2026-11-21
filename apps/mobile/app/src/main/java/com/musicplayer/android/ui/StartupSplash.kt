package com.musicplayer.android.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import com.musicplayer.android.R
import com.musicplayer.android.ui.theme.HathorColors

/** Dark startup screen shown while the first library/history preload runs. */
@Composable
fun StartupSplashScreen() {
    val transition = rememberInfiniteTransition(label = "splash-pulse")
    val logoAlpha by transition.animateFloat(
        initialValue = 0.75f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 900),
            repeatMode = RepeatMode.Reverse
        ),
        label = "splash-logo-alpha"
    )
    Box(
        modifier = Modifier.fillMaxSize().background(HathorColors.Background),
        contentAlignment = Alignment.Center
    ) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(22.dp)
        ) {
            Image(
                painter = painterResource(id = R.drawable.app_logo),
                contentDescription = "Hathor",
                modifier = Modifier.size(132.dp).alpha(logoAlpha)
            )
            CircularProgressIndicator(
                modifier = Modifier.size(30.dp),
                color = HathorColors.AccentBright,
                strokeWidth = 3.dp
            )
            Text(
                "Loading your library…",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
        }
    }
}
