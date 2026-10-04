package com.musicplayer.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import com.musicplayer.android.ui.theme.HathorColors

/**
 * Shared modal chrome (the Lyrics sheet already worked this way): title on
 * the left, X close on the right. Every AlertDialog uses this instead of a
 * bottom "Close"/"Cancel" text button, so dismiss is always top-right.
 * (The dialog keeps the Material3 headlineSmall title style — only the
 * layout and the X are added.)
 */
@Composable
fun DialogTitleBar(title: String, onClose: () -> Unit, closeDesc: String = "Close") {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            title,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f)
        )
        IconButton(onClick = onClose) {
            Icon(Icons.Filled.Close, contentDescription = closeDesc, tint = HathorColors.TextHint)
        }
    }
}
