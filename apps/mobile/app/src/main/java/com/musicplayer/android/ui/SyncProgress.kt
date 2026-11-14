package com.musicplayer.android.ui

import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.work.WorkInfo
import androidx.work.WorkManager
import com.musicplayer.android.data.remote.PullWorker
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.hathorGlass

data class SyncProgress(
    val text: String,
    val done: Int = 0,
    val total: Int = 0,
    val running: Boolean = false
) {
    val fraction: Float?
        get() = if (total > 0) (done.toFloat() / total).coerceIn(0f, 1f) else null
}

/** WorkManager's pull progress, safe to call off the main thread. */
fun querySyncProgress(context: Context): SyncProgress? {
    return try {
        val info = WorkManager.getInstance(context)
            .getWorkInfosForUniqueWork(PullWorker.UNIQUE_NAME)
            .get()
            .firstOrNull() ?: return null
        when (info.state) {
            WorkInfo.State.ENQUEUED -> SyncProgress("Pull queued — starts when online.", running = true)
            WorkInfo.State.RUNNING -> {
                val step = info.progress.getString("step") ?: "Syncing in background…"
                val done = info.progress.getInt("done", 0)
                val total = info.progress.getInt("total", 0)
                SyncProgress(
                    text = if (total > 0) "$step · $done/$total" else step,
                    done = done,
                    total = total,
                    running = true
                )
            }
            WorkInfo.State.SUCCEEDED -> SyncProgress(
                text = info.outputData.getString("summary") ?: "Pull finished."
            )
            WorkInfo.State.FAILED -> SyncProgress(
                text = "Background pull failed: " +
                    (info.outputData.getString("error") ?: "unknown error")
            )
            WorkInfo.State.CANCELLED -> SyncProgress("Background pull cancelled.")
            else -> null
        }
    } catch (_: Exception) {
        null
    }
}

/** Thin bar shown under the shell header while a pull is active. */
@Composable
fun SyncProgressBar(progress: SyncProgress, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp)
            .hathorGlass().padding(horizontal = 10.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Text(
            progress.text,
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint
        )
        val fraction = progress.fraction
        if (fraction != null) {
            LinearProgressIndicator(
                progress = { fraction },
                modifier = Modifier.fillMaxWidth(),
                color = HathorColors.Accent
            )
        } else {
            LinearProgressIndicator(
                modifier = Modifier.fillMaxWidth(),
                color = HathorColors.Accent
            )
        }
    }
}
