package com.musicplayer.android.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Sync
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.SettingsRepository
import com.musicplayer.android.data.SyncRepository
import com.musicplayer.android.data.remote.PullWorker
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorSwitch
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.ModernSlider
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.launch

/**
 * Settings destination: everything in one place (desktop settings view +
 * the remote/local sync actions, which live here on desktop too — no
 * separate sync screen). Volume also lives on the NowPlaying sheet as the
 * mobile adaptation; both drive the same stream.
 */
@Composable
fun SettingsScreen(
    settings: SettingsRepository,
    sync: SyncRepository,
    podcasts: com.musicplayer.android.data.PodcastRepository,
    onPull: () -> Unit
) {
    val volume by settings.volume.collectAsState()
    val limit by settings.limit.collectAsState()
    val syncState by sync.state.collectAsState()
    var rescanResult by remember { mutableStateOf<String?>(null) }
    var folderResult by remember { mutableStateOf<String?>(null) }
    var folderPath by remember { mutableStateOf(settings.musicFolder()) }
    var podRescanResult by remember { mutableStateOf<String?>(null) }
    var podFolderResult by remember { mutableStateOf<String?>(null) }
    var podFolderPath by remember { mutableStateOf(settings.podcastsFolder()) }
    var busy by remember { mutableStateOf(false) }
    // Background pull status (first-run dialog and this button share one worker).
    var pullStatus by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val prefs = remember {
        context.getSharedPreferences("hathor", android.content.Context.MODE_PRIVATE)
    }
    // Desktop parity default: linkless rows fall back to a title search.
    var searchFallback by remember { mutableStateOf(prefs.getBoolean("search_fallback", true)) }

    LaunchedEffect(Unit) {
        settings.load()
        folderPath = settings.musicFolder()
        // Poll the pull worker; the dialog/screen never block on downloads.
        while (true) {
            pullStatus = queryPullStatus(context)
            kotlinx.coroutines.delay(2000)
        }
    }

    val pickFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            folderResult = settings.changeFolder(uri)
            folderPath = settings.musicFolder()
        }
    }
    val pickPodcastsFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            podFolderResult = settings.changePodcastsFolder(uri)
            podFolderPath = settings.podcastsFolder()
        }
    }
    var wallpaperMsg by remember { mutableStateOf<String?>(null) }
    val pickWallpaper = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch { wallpaperMsg = settings.setBackground(uri) }
    }

    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            HeaderIconTile {
                Icon(Icons.Filled.Settings, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column {
                Text("Settings", style = MaterialTheme.typography.titleLarge)
                Text("Manage your preferences", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }
        }

        // ---- Remote sync (desktop settings view owns these actions) ----
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            SectionLabel(Icons.Filled.Sync, "Remote sync")
            Text(settings.remoteStatus(), style = MaterialTheme.typography.bodySmall)
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { onPull() },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text("Pull") }
                Button(
                    onClick = { scope.launch { sync.pushToRemote() } },
                    enabled = syncState !is SyncRepository.SyncState.Running,
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text("Push") }
            }
            pullStatus?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Search YouTube for songs without links", style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "Off = linkless rows are listed but skipped, never guessed.",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                HathorSwitch(
                    checked = searchFallback,
                    onCheckedChange = {
                        searchFallback = it
                        prefs.edit().putBoolean("search_fallback", it).apply()
                    }
                )
            }
            when (val s = syncState) {
                is SyncRepository.SyncState.Running -> Text(s.step, style = MaterialTheme.typography.bodySmall)
                is SyncRepository.SyncState.Done -> Text(s.summary, style = MaterialTheme.typography.bodySmall)
                is SyncRepository.SyncState.Error -> Text("FAILED: ${s.message}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                else -> {}
            }
        }

        // ---- Volume ----
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            SectionLabel(Icons.Filled.VolumeUp, "Volume (${(volume * 100).toInt()}%)")
            ModernSlider(
                value = volume,
                onValueChange = { settings.setVolume(it) },
                onValueChangeFinished = { scope.launch { settings.persistVolume() } },
                modifier = Modifier.fillMaxWidth()
            )
        }

        // ---- Crossfade (desktop Settings toggle + 1-12s, default off / 5s) ----
        // Gapless handoff is always on; crossfade blends auto-advance only.
        val xEnabled by settings.crossfadeEnabled.collectAsState()
        val xSecs by settings.crossfadeSeconds.collectAsState()
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Crossfade (${xSecs.toInt()}s)", style = MaterialTheme.typography.titleSmall)
                    Text(
                        "Blend into the next song on auto-advance. Manual next/prev stay instant.",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                HathorSwitch(
                    checked = xEnabled,
                    onCheckedChange = { scope.launch { settings.setCrossfade(it, xSecs) } }
                )
            }
            ModernSlider(
                value = ((xSecs - 1f) / 11f).coerceIn(0f, 1f),
                onValueChange = { frac ->
                    scope.launch { settings.setCrossfade(xEnabled, 1f + frac * 11f) }
                },
                enabled = xEnabled,
                modifier = Modifier.fillMaxWidth()
            )
        }

        // ---- Chapter auto-skip (desktop Settings toggle, podcasts only) ----
        // Timestamped episodes start at the first chapter and skip to the
        // next chapter when one ends. Chapters are managed per episode.
        val chapterSkip by settings.chapterSkip.collectAsState()
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Chapter auto-skip", style = MaterialTheme.typography.titleSmall)
                    Text(
                        "Podcast episodes start at the first chapter and skip ahead at chapter ends.",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                HathorSwitch(
                    checked = chapterSkip,
                    onCheckedChange = { scope.launch { settings.setChapterSkip(it) } }
                )
            }
        }

        // ---- Concurrency ----
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            SectionLabel(Icons.Filled.Download, "Concurrent downloads ($limit)")
            Text("Same limit as desktop (1–20). Applies to new jobs.", style = MaterialTheme.typography.bodySmall)
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(onClick = { scope.launch { settings.setLimit(limit - 1) } }, enabled = limit > 1) { Text("−") }
                Text("$limit", style = MaterialTheme.typography.titleMedium)
                Button(onClick = { scope.launch { settings.setLimit(limit + 1) } }, enabled = limit < 20) { Text("+") }
            }
        }

        // ---- Wallpaper (desktop background picker, same 40% scrim) ----
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            SectionLabel(Icons.Filled.Image, "Wallpaper")
            Text("Dimmed 40% behind the UI, like the desktop.", style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { pickWallpaper.launch("image/*") },
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text("Choose") }
                Button(
                    onClick = { scope.launch { wallpaperMsg = settings.removeBackground() } },
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text("Remove") }
            }
            wallpaperMsg?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }

        // ---- Folder + rescan ----
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            SectionLabel(Icons.Filled.Folder, "Music folder")
            Text(folderPath, style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { pickFolder.launch(null) },
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text("Change") }
                Button(
                    onClick = {
                        busy = true
                        rescanResult = null
                        scope.launch {
                            rescanResult = settings.rescan()
                            busy = false
                        }
                    },
                    enabled = !busy,
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text(if (busy) "Scanning…" else "Rescan folder") }
            }
            folderResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            rescanResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }

        // ---- Podcasts folder + rescan (desktop Podcasts Folder row) ----
        Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            SectionLabel(Icons.Filled.Folder, "Podcasts folder")
            Text(podFolderPath, style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { pickPodcastsFolder.launch(null) },
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text("Change") }
                Button(
                    onClick = {
                        busy = true
                        podRescanResult = null
                        scope.launch {
                            podRescanResult = podcasts.rescan()
                            busy = false
                        }
                    },
                    enabled = !busy,
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
                ) { Text(if (busy) "Scanning…" else "Rescan podcasts") }
            }
            podFolderResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            podRescanResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }
    }
}
@Composable
private fun SectionLabel(icon: androidx.compose.ui.graphics.vector.ImageVector, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Icon(icon, contentDescription = null, tint = HathorColors.AccentBright)
        Text(text, style = MaterialTheme.typography.titleSmall)
    }
}

/** Latest pull-worker state for the Settings section (null when never run). */
private fun queryPullStatus(context: android.content.Context): String? {
    return try {
        val infos = androidx.work.WorkManager.getInstance(context)
            .getWorkInfosForUniqueWork(PullWorker.UNIQUE_NAME).get()
        val info = infos.firstOrNull() ?: return null
        when (info.state) {
            androidx.work.WorkInfo.State.ENQUEUED -> "Pull queued — starts when online."
            androidx.work.WorkInfo.State.RUNNING -> {
                val step = info.progress.getString("step")
                val total = info.progress.getInt("total", 0)
                val done = info.progress.getInt("done", 0)
                if (total > 0) "Syncing in background… $step ($done/$total)"
                else step?.takeIf { it.isNotBlank() }?.let { "Syncing in background… $it" }
                    ?: "Syncing in background…"
            }
            androidx.work.WorkInfo.State.SUCCEEDED ->
                info.outputData.getString("summary") ?: "Pull finished."
            androidx.work.WorkInfo.State.FAILED ->
                "Background pull failed: ${info.outputData.getString("error") ?: "unknown"}"
            androidx.work.WorkInfo.State.CANCELLED -> "Background pull cancelled."
            else -> null
        }
    } catch (_: Exception) {
        null
    }
}
