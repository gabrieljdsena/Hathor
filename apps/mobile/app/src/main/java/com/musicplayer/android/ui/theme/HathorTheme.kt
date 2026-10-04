package com.musicplayer.android.ui.theme

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.background

/**
 * Hathor visual system, sampled from the desktop CSS (ui/index.html + views):
 * zinc-950 base, orange-500 accent, glass surfaces, rounded-2xl cards.
 */
object HathorColors {
    val Background = Color(0xFF09090B)      // zinc-950
    val SurfaceGlass = Color(0x66000000)    // black/40
    val BorderFaint = Color(0x0DFFFFFF)     // white/5
    val Accent = Color(0xFFF97316)          // orange-500
    val AccentBright = Color(0xFFFB923C)    // orange-400
    val AccentDeep = Color(0xFFEA580C)      // orange-600
    val AccentFill = Color(0x1AF97316)      // orange-500/10 (active nav, tiles)
    val TextPrimary = Color(0xFFF4F4F5)     // zinc-100
    val TextBody = Color(0xFFD4D4D8)        // zinc-300
    val TextHint = Color(0xFF71717A)        // zinc-500

    val BrandGradient = Brush.linearGradient(listOf(AccentBright, AccentDeep))
    // Ambient background glows (10% orange, desktop body::before equivalents).
    val GlowTop = Color(0x1AF97316)
    val GlowBottom = Color(0x1AEA580C)
}

private val HathorScheme = darkColorScheme(
    primary = HathorColors.Accent,
    onPrimary = Color.White,
    secondary = HathorColors.AccentBright,
    background = HathorColors.Background,
    onBackground = HathorColors.TextBody,
    surface = Color(0xFF131316),
    onSurface = HathorColors.TextPrimary,
    surfaceVariant = Color(0xFF1B1B1F),
    onSurfaceVariant = HathorColors.TextBody,
    outline = HathorColors.BorderFaint
)

private val HathorType = Typography(
    titleLarge = TextStyle(fontWeight = FontWeight.Bold, fontSize = 22.sp, color = HathorColors.TextPrimary),
    titleMedium = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 18.sp, color = HathorColors.TextPrimary),
    titleSmall = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 14.sp, color = HathorColors.TextPrimary),
    bodyMedium = TextStyle(fontSize = 14.sp, color = HathorColors.TextBody),
    bodySmall = TextStyle(fontSize = 12.sp, color = HathorColors.TextHint)
)

@Composable
fun HathorTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = HathorScheme, typography = HathorType, content = content)
}

/** Glass card: black/40 surface, faint border, 16dp radius (desktop rounded-2xl). */
fun Modifier.hathorGlass(): Modifier =
    this
        .clip(RoundedCornerShape(16.dp))
        .background(HathorColors.SurfaceGlass)

/**
 * Desktop section-header icon tile:
 * 48dp, orange-500/10 fill, orange-500/20 border, orange icon slot.
 */
@Composable
fun HeaderIconTile(content: @Composable () -> Unit) {
    Box(
        modifier = Modifier
            .size(48.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(HathorColors.AccentFill),
        contentAlignment = Alignment.Center
    ) { content() }
}

/**
 * Custom switch (desktop themed toggle, not a native checkbox): gradient
 * track + glow when on, dim track when off, animated thumb.
 */
@Composable
fun HathorSwitch(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true
) {
    val anim by androidx.compose.animation.core.animateFloatAsState(
        targetValue = if (checked) 1f else 0f,
        animationSpec = androidx.compose.animation.core.tween(
            durationMillis = 300, easing = FastOutSlowInEasing
        ),
        label = "switch"
    )
    val thumbOffset by androidx.compose.animation.core.animateDpAsState(
        targetValue = if (checked) 24.dp else 2.dp,
        animationSpec = androidx.compose.animation.core.tween(
            durationMillis = 300, easing = FastOutSlowInEasing
        ),
        label = "switch-thumb"
    )
    val alpha = if (enabled) 1f else 0.4f
    Box(
        modifier = modifier
            .size(52.dp, 30.dp)
            .then(
                if (checked) Modifier.shadow(
                    elevation = 8.dp,
                    shape = RoundedCornerShape(15.dp),
                    spotColor = HathorColors.Accent.copy(alpha = 0.6f)
                ) else Modifier
            )
            .clip(RoundedCornerShape(15.dp))
            .then(
                if (checked) Modifier.background(HathorColors.BrandGradient)
                else Modifier.background(HathorColors.TextHint.copy(alpha = 0.3f))
            )
            .alpha(alpha)
            .clickable(enabled = enabled) { onCheckedChange(!checked) },
        contentAlignment = Alignment.CenterStart
    ) {
        Box(
            modifier = Modifier
                .offset(x = thumbOffset)
                .size(26.dp)
                .clip(androidx.compose.foundation.shape.CircleShape)
                .background(Color.White)
        )
    }
}

/**
 * Modern rounded glass input (single shared style for search bars, dialog
 * fields and forms): 16dp radius, translucent fill, faint border that glows
 * orange on focus — the desktop search-pill language.
 */
@Composable
fun HathorTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    label: String? = null,
    singleLine: Boolean = true,
    leadingIcon: @Composable (() -> Unit)? = null,
    trailingIcon: @Composable (() -> Unit)? = null
) {
    androidx.compose.material3.OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = label?.let { { Text(it) } },
        leadingIcon = leadingIcon,
        trailingIcon = trailingIcon,
        singleLine = singleLine,
        shape = RoundedCornerShape(16.dp),
        colors = androidx.compose.material3.TextFieldDefaults.colors(
            focusedContainerColor = HathorColors.SurfaceGlass,
            unfocusedContainerColor = HathorColors.SurfaceGlass,
            disabledContainerColor = HathorColors.SurfaceGlass,
            focusedIndicatorColor = HathorColors.Accent,
            unfocusedIndicatorColor = HathorColors.BorderFaint,
            disabledIndicatorColor = HathorColors.BorderFaint,
            cursorColor = HathorColors.AccentBright,
            focusedLabelColor = HathorColors.AccentBright,
            unfocusedLabelColor = HathorColors.TextHint,
            focusedLeadingIconColor = HathorColors.AccentBright,
            unfocusedLeadingIconColor = HathorColors.TextHint,
            focusedTrailingIconColor = HathorColors.AccentBright,
            unfocusedTrailingIconColor = HathorColors.TextHint,
            focusedTextColor = HathorColors.TextPrimary,
            unfocusedTextColor = HathorColors.TextBody
        ),
        modifier = modifier
    )
}
