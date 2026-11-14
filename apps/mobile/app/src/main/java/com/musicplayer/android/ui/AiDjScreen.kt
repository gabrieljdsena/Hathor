package com.musicplayer.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.api.AiDjApi
import com.musicplayer.android.data.api.AiDjBatch
import com.musicplayer.android.data.api.AiDjState
import com.musicplayer.android.data.api.AiDjTrack
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * AI DJ (Vohithe) screen. All selection/commentary logic is server-side; this
 * screen starts/stops sessions, shows batch metadata and asks the host to
 * play the returned local files.
 */
@Composable
fun AiDjScreen(
    onPlayBatch: (List<AiDjTrack>, String) -> Boolean
) {
    var state by remember { mutableStateOf<AiDjState?>(null) }
    var brief by remember { mutableStateOf("") }
    var loading by remember { mutableStateOf(true) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var notice by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun refresh() {
        try {
            state = AiDjApi.state()
            loading = false
        } catch (e: Exception) {
            loading = false
            error = e.message ?: "Could not reach the AI DJ."
        }
    }

    LaunchedEffect(Unit) {
        refresh()
        while (true) {
            delay(5_000)
            try {
                state = AiDjApi.state()
            } catch (_: Exception) {
                // Keep the last known state while offline.
            }
        }
    }

    fun start() {
        scope.launch {
            busy = true
            error = null
            notice = null
            try {
                val s = AiDjApi.start(brief)
                state = s
                val batch = s.currentBatch
                if (batch == null || batch.tracks.isEmpty()) {
                    error = "The DJ did not return a playable first set."
                } else if (!onPlayBatch(batch.tracks, s.sessionId.orEmpty())) {
                    error = "The DJ selected tracks that are not downloaded on this device. Pull the library first."
                } else {
                    notice = "Set started."
                    brief = ""
                }
            } catch (e: Exception) {
                error = e.message ?: "Could not start the AI DJ."
            } finally {
                busy = false
            }
        }
    }

    fun playBatch(batch: AiDjBatch?) {
        if (batch == null || batch.tracks.isEmpty()) return
        val session = state?.sessionId.orEmpty()
        if (onPlayBatch(batch.tracks, session)) {
            notice = "Playing batch ${batch.number}."
            error = null
        } else {
            error = "Those tracks are not downloaded on this device. Pull the library first."
        }
    }

    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            HeaderIconTile {
                Icon(Icons.Filled.AutoAwesome, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text("AI DJ", style = MaterialTheme.typography.titleLarge)
                Text(
                    state?.status?.replaceFirstChar { it.uppercase() } ?: "Vohithe",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            }
            TextButton(onClick = { scope.launch { refresh() } }) { Text("Refresh") }
        }

        val current = state
        if (loading) {
            Text("Connecting to the AI DJ…", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
        if (current != null) {
            if (!current.enabled) {
                Text(
                    "AI DJ is disabled on the server. Enable it in the Web API configuration.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            } else if (!current.configured) {
                Text(
                    "AI DJ is enabled but not configured on the server (assistant URL/token missing).",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            }
            current.error?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
        }

        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("What should the DJ play?", style = MaterialTheme.typography.titleSmall)
            HathorTextField(
                value = brief,
                onValueChange = { brief = it },
                label = "Vibe brief (optional)",
                modifier = Modifier.fillMaxWidth()
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { start() },
                    enabled = !busy && current?.enabled == true,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White
                    )
                ) { Text(if (current?.sessionId == null) "Start set" else "New set") }
                if (current?.sessionId != null) {
                    OutlinedButton(
                        onClick = {
                            scope.launch {
                                busy = true
                                try {
                                    state = AiDjApi.stop()
                                    notice = "DJ stopped."
                                } catch (e: Exception) {
                                    error = e.message ?: "Could not stop the DJ."
                                } finally {
                                    busy = false
                                }
                            }
                        },
                        enabled = !busy
                    ) { Text("Stop") }
                }
                if (current?.sessionId != null) {
                    TextButton(onClick = {
                        scope.launch { runCatching { AiDjApi.feedback(current.sessionId, current.currentBatch?.id, null, "like") } }
                    }) { Text("More like this") }
                    TextButton(onClick = {
                        scope.launch { runCatching { AiDjApi.feedback(current.sessionId, current.currentBatch?.id, null, "dislike") } }
                    }) { Text("Less like this") }
                }
            }
            error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
            notice?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint) }
        }

        current?.currentBatch?.let { batch ->
            BatchCard(batch = batch, label = "On air", onPlay = { playBatch(batch) })
        }
        current?.nextBatch?.let { batch ->
            BatchCard(batch = batch, label = "Next batch ready", onPlay = { playBatch(batch) })
        }
        if (current?.currentBatch == null && current?.nextBatch == null && current?.sessionId != null) {
            Text("Waiting for the first batch…", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
    }
}

@Composable
private fun BatchCard(batch: AiDjBatch, label: String, onPlay: () -> Unit) {
    Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(modifier = Modifier.weight(1f)) {
                Text("$label · batch ${batch.number}", style = MaterialTheme.typography.titleSmall)
                batch.mood?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
                }
            }
            Button(
                onClick = onPlay,
                colors = ButtonDefaults.buttonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = null)
                Text(" Play")
            }
        }
        batch.reason?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
        batch.introText?.let {
            Text("“$it”", style = MaterialTheme.typography.bodyMedium)
        }
        batch.tracks.forEach { track ->
            Text(
                "${track.title.ifBlank { track.file }} · ${track.artist.ifBlank { "Unknown" }}",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextBody
            )
        }
        batch.outroText?.let {
            Text("Outro: $it", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
    }
}
