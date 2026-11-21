package com.musicplayer.android.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.musicplayer.android.BuildConfig
import com.musicplayer.android.data.update.UpdateState
import com.musicplayer.android.ui.theme.HathorColors
import java.util.Locale

/** Sideload updater dialog: available, progress, install permission and retry. */
@Composable
fun UpdateDialog(
    state: UpdateState,
    onDismiss: () -> Unit,
    onUpdate: () -> Unit,
    onInstall: () -> Unit,
    onRetry: () -> Unit,
) {
    val accentButton: @Composable (String, () -> Unit) -> Unit = { label, action ->
        Button(
            onClick = action,
            colors = ButtonDefaults.buttonColors(
                containerColor = HathorColors.Accent,
                contentColor = Color.White,
            ),
        ) { Text(label) }
    }

    when (state) {
        is UpdateState.Available -> AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text("Update available") },
            text = {
                Column {
                    Text("Hathor ${BuildConfig.VERSION_NAME} → ${state.info.versionName}")
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "Build ${state.info.versionCode} · ${formatBytes(state.info.sizeBytes)}",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint,
                    )
                }
            },
            confirmButton = { accentButton("Update", onUpdate) },
            dismissButton = { TextButton(onClick = onDismiss) { Text("Later") } },
        )

        is UpdateState.Downloading -> {
            val total = state.total.coerceAtLeast(1L)
            val fraction = (state.bytes.toFloat() / total.toFloat()).coerceIn(0f, 1f)
            AlertDialog(
                onDismissRequest = onDismiss,
                title = { Text("Downloading update") },
                text = {
                    Column {
                        Text("Hathor ${state.info.versionName}")
                        Spacer(Modifier.height(12.dp))
                        LinearProgressIndicator(
                            progress = { fraction },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Spacer(Modifier.height(8.dp))
                        Text(
                            "${(fraction * 100).toInt()}% · " +
                                "${formatBytes(state.bytes)} of ${formatBytes(state.total)}",
                            style = MaterialTheme.typography.bodySmall,
                            color = HathorColors.TextHint,
                        )
                    }
                },
                confirmButton = {},
                dismissButton = { TextButton(onClick = onDismiss) { Text("Hide") } },
            )
        }

        is UpdateState.Ready -> AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text("Update ready") },
            text = {
                Column {
                    Text("${state.info.versionName} is downloaded and verified.")
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "Android will ask you to confirm the install.",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint,
                    )
                }
            },
            confirmButton = { accentButton("Install", onInstall) },
            dismissButton = { TextButton(onClick = onDismiss) { Text("Later") } },
        )

        is UpdateState.NeedsPermission -> AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text("Allow app installs") },
            text = {
                Column {
                    Text("Android needs permission to install Hathor updates from this app.")
                    Spacer(Modifier.height(6.dp))
                    Text(
                        "Enable “Allow from this source”, then return to Hathor.",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint,
                    )
                }
            },
            confirmButton = { accentButton("Open settings", onInstall) },
            dismissButton = { TextButton(onClick = onDismiss) { Text("Later") } },
        )

        is UpdateState.Error -> AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text("Update failed") },
            text = { Text(state.message) },
            confirmButton = { accentButton("Retry", onRetry) },
            dismissButton = { TextButton(onClick = onDismiss) { Text("Later") } },
        )

        else -> Unit
    }
}

private fun formatBytes(bytes: Long): String =
    if (bytes <= 0L) "unknown size"
    else String.format(Locale.US, "%.1f MB", bytes / 1024.0 / 1024.0)
