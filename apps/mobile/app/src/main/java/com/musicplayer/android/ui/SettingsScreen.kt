package com.musicplayer.android.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Sync
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.musicplayer.android.BuildConfig
import com.musicplayer.android.data.SettingsRepository
import com.musicplayer.android.data.SyncRepository
import com.musicplayer.android.data.api.HathorApi
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.remote.RemoteSync
import com.musicplayer.android.data.update.UpdateRepository
import com.musicplayer.android.data.update.UpdateState
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorSwitch
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HathorTonalButton
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.ModernSlider
import com.musicplayer.android.ui.theme.hathorGlass
import com.musicplayer.android.ui.theme.hathorPressable
import kotlinx.coroutines.launch

/**
 * Settings destination: the same actions as the desktop settings view, split
 * into five collapsible groups so the page stays short on a phone.
 */
@Composable
fun SettingsScreen(
    settings: SettingsRepository,
    sync: SyncRepository,
    podcasts: com.musicplayer.android.data.PodcastRepository,
    updateRepo: UpdateRepository,
    onPull: () -> Unit,
    wallpaper: android.graphics.Bitmap? = null
) {
    val limit by settings.limit.collectAsState()
    val syncState by sync.state.collectAsState()
    val updateState by updateRepo.state.collectAsState()
    val xEnabled by settings.crossfadeEnabled.collectAsState()
    val xSecs by settings.crossfadeSeconds.collectAsState()
    val chapterSkip by settings.chapterSkip.collectAsState()
    var openGroup by remember { mutableStateOf<SettingsGroup?>(SettingsGroup.Connection) }
    var rescanResult by remember { mutableStateOf<String?>(null) }
    var folderResult by remember { mutableStateOf<String?>(null) }
    var folderPath by remember { mutableStateOf(settings.musicFolder()) }
    var podRescanResult by remember { mutableStateOf<String?>(null) }
    var podFolderResult by remember { mutableStateOf<String?>(null) }
    var podFolderPath by remember { mutableStateOf(settings.podcastsFolder()) }
    var busy by remember { mutableStateOf(false) }
    var apiUrl by remember { mutableStateOf(SyncConfig.baseUrl()) }
    var apiToken by remember { mutableStateOf(SyncConfig.token()) }
    var apiMessage by remember { mutableStateOf<String?>(null) }
    var testingApi by remember { mutableStateOf(false) }
    // Background pull status (first-run dialog and this button share one worker).
    var pullProgress by remember { mutableStateOf<SyncProgress?>(null) }
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
            pullProgress = querySyncProgress(context)
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

    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .widthIn(max = 820.dp)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            HeaderIconTile {
                Icon(Icons.Filled.Settings, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Text("Settings", style = MaterialTheme.typography.titleLarge)
        }

        // ---- Connection ----
        SettingsGroupCard(
            group = SettingsGroup.Connection,
            expanded = openGroup == SettingsGroup.Connection,
            onToggle = {
                openGroup = if (openGroup == SettingsGroup.Connection) null else SettingsGroup.Connection
            }
        ) {
            SectionLabel(Icons.Filled.Sync, "Web API")
            HathorTextField(
                value = apiUrl,
                onValueChange = { apiUrl = it },
                label = "Server URL (http://host:5051)",
                modifier = Modifier.fillMaxWidth()
            )
            HathorTextField(
                value = apiToken,
                onValueChange = { apiToken = it },
                label = "API key (hth_…)",
                visualTransformation = PasswordVisualTransformation(),
                modifier = Modifier.fillMaxWidth()
            )
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = {
                        SyncConfig.save(apiUrl, apiToken)
                        apiUrl = SyncConfig.baseUrl()
                        apiToken = SyncConfig.token()
                        sync.scheduleHistoryPush()
                        apiMessage = "Saved."
                    },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White
                    )
                ) { Text("Save") }
                HathorTonalButton(
                    onClick = {
                        testingApi = true
                        apiMessage = null
                        scope.launch {
                            apiMessage = HathorApi.testConnection(apiUrl, apiToken)
                            testingApi = false
                        }
                    },
                    enabled = !testingApi,
                    modifier = Modifier.weight(1f)
                ) { Text(if (testingApi) "Testing…" else "Test") }
            }
            apiMessage?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }

            GroupDivider()
            SectionLabel(Icons.Filled.Sync, "Sync server")
            Text(settings.remoteStatus(), style = MaterialTheme.typography.bodySmall)
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { onPull() },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White
                    )
                ) { Text("Pull") }
                Button(
                    onClick = { scope.launch { sync.pushToRemote() } },
                    enabled = syncState !is SyncRepository.SyncState.Running,
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White
                    )
                ) { Text("Push") }
            }
            pullProgress?.let { progress ->
                if (progress.running) {
                    SyncProgressBar(progress)
                } else {
                    Text(progress.text, style = MaterialTheme.typography.bodySmall)
                }
            }
            TextButton(
                onClick = {
                    RemoteSync.resetCursor(context)
                    onPull()
                    pullProgress = SyncProgress(
                        "Full resync queued — retrying missing files…",
                        running = true
                    )
                }
            ) { Text("Full resync files") }
            when (val s = syncState) {
                is SyncRepository.SyncState.Running ->
                    Text(s.step, style = MaterialTheme.typography.bodySmall)
                is SyncRepository.SyncState.Done ->
                    Text(s.summary, style = MaterialTheme.typography.bodySmall)
                is SyncRepository.SyncState.Error ->
                    Text(
                        "FAILED: ${s.message}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error
                    )
                else -> {}
            }
        }

        // ---- Playback ----
        SettingsGroupCard(
            group = SettingsGroup.Playback,
            expanded = openGroup == SettingsGroup.Playback,
            onToggle = {
                openGroup = if (openGroup == SettingsGroup.Playback) null else SettingsGroup.Playback
            }
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        "Search YouTube for songs without links",
                        style = MaterialTheme.typography.bodyMedium
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

            GroupDivider()
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("Crossfade (${xSecs.toInt()}s)", style = MaterialTheme.typography.titleSmall)
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

            GroupDivider()
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("Chapter auto-skip", style = MaterialTheme.typography.titleSmall)
                HathorSwitch(
                    checked = chapterSkip,
                    onCheckedChange = { scope.launch { settings.setChapterSkip(it) } }
                )
            }

            GroupDivider()
            SectionLabel(Icons.Filled.Download, "Concurrent downloads ($limit)")
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                HathorTonalButton(
                    onClick = { scope.launch { settings.setLimit(limit - 1) } },
                    enabled = limit > 1
                ) { Text("−") }
                Text("$limit", style = MaterialTheme.typography.titleMedium)
                HathorTonalButton(
                    onClick = { scope.launch { settings.setLimit(limit + 1) } },
                    enabled = limit < 20
                ) { Text("+") }
            }
        }

        // ---- Appearance ----
        SettingsGroupCard(
            group = SettingsGroup.Appearance,
            expanded = openGroup == SettingsGroup.Appearance,
            onToggle = {
                openGroup = if (openGroup == SettingsGroup.Appearance) null else SettingsGroup.Appearance
            }
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                if (wallpaper != null) {
                    Image(
                        bitmap = wallpaper.asImageBitmap(),
                        contentDescription = "Wallpaper preview",
                        modifier = Modifier.size(92.dp).clip(RoundedCornerShape(12.dp)),
                        contentScale = ContentScale.Crop
                    )
                } else {
                    Box(
                        modifier = Modifier
                            .size(92.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(HathorColors.AccentFill),
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(
                            Icons.Filled.Image,
                            contentDescription = null,
                            tint = HathorColors.AccentBright,
                            modifier = Modifier.size(30.dp)
                        )
                    }
                }
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text("Wallpaper", style = MaterialTheme.typography.titleSmall)
                    Text(
                        if (wallpaper == null) "Default" else "Custom",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint
                    )
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                HathorTonalButton(
                    onClick = { pickWallpaper.launch("image/*") }
                ) { Text("Choose") }
                HathorTonalButton(
                    onClick = { scope.launch { wallpaperMsg = settings.removeBackground() } }
                ) { Text("Remove") }
            }
            wallpaperMsg?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }

        // ---- Storage ----
        SettingsGroupCard(
            group = SettingsGroup.Storage,
            expanded = openGroup == SettingsGroup.Storage,
            onToggle = {
                openGroup = if (openGroup == SettingsGroup.Storage) null else SettingsGroup.Storage
            }
        ) {
            SectionLabel(Icons.Filled.Folder, "Music folder")
            Text(folderPath, style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                HathorTonalButton(
                    onClick = { pickFolder.launch(null) }
                ) { Text("Change") }
                HathorTonalButton(
                    onClick = {
                        busy = true
                        rescanResult = null
                        scope.launch {
                            rescanResult = settings.rescan()
                            busy = false
                        }
                    },
                    enabled = !busy
                ) { Text(if (busy) "Scanning…" else "Rescan folder") }
            }
            folderResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            rescanResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }

            GroupDivider()
            SectionLabel(Icons.Filled.Folder, "Podcasts folder")
            Text(podFolderPath, style = MaterialTheme.typography.bodySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                HathorTonalButton(
                    onClick = { pickPodcastsFolder.launch(null) }
                ) { Text("Change") }
                HathorTonalButton(
                    onClick = {
                        busy = true
                        podRescanResult = null
                        scope.launch {
                            podRescanResult = podcasts.rescan()
                            busy = false
                        }
                    },
                    enabled = !busy
                ) { Text(if (busy) "Scanning…" else "Rescan podcasts") }
            }
            podFolderResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            podRescanResult?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }

        // ---- About ----
        SettingsGroupCard(
            group = SettingsGroup.About,
            expanded = openGroup == SettingsGroup.About,
            onToggle = {
                openGroup = if (openGroup == SettingsGroup.About) null else SettingsGroup.About
            }
        ) {
            Text(
                "Hathor ${BuildConfig.VERSION_NAME} (build ${BuildConfig.VERSION_CODE})",
                style = MaterialTheme.typography.titleSmall
            )
            val statusText = updateStatusText(updateState)
            if (statusText.isNotBlank()) {
                Text(
                    statusText,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (updateState is UpdateState.Error) {
                        MaterialTheme.colorScheme.error
                    } else {
                        HathorColors.TextHint
                    }
                )
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                HathorTonalButton(
                    onClick = { updateRepo.check(force = true) },
                    enabled = updateState !is UpdateState.Checking &&
                        updateState !is UpdateState.Downloading &&
                        updateState !is UpdateState.Installing,
                ) { Text("Check for updates") }
                when (updateState) {
                    is UpdateState.Available -> Button(
                        onClick = updateRepo::download,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = HathorColors.Accent,
                            contentColor = Color.White,
                        ),
                    ) { Text("Update") }
                    is UpdateState.Ready, is UpdateState.NeedsPermission -> Button(
                        onClick = updateRepo::install,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = HathorColors.Accent,
                            contentColor = Color.White,
                        ),
                    ) { Text("Install") }
                    is UpdateState.Error -> Button(
                        onClick = updateRepo::retry,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = HathorColors.Accent,
                            contentColor = Color.White,
                        ),
                    ) { Text("Retry") }
                    else -> Unit
                }
            }
        }
        }
    }
}

private enum class SettingsGroup(val title: String, val icon: ImageVector) {
    Connection("Connection", Icons.Filled.Sync),
    Playback("Playback", Icons.Filled.PlayArrow),
    Appearance("Appearance", Icons.Filled.Image),
    Storage("Storage", Icons.Filled.Folder),
    About("About", Icons.Filled.Info)
}

@Composable
private fun SettingsGroupCard(
    group: SettingsGroup,
    expanded: Boolean,
    onToggle: () -> Unit,
    content: @Composable ColumnScope.() -> Unit
) {
    Column(modifier = Modifier.fillMaxWidth().hathorGlass()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .hathorPressable(onToggle)
                .padding(horizontal = 14.dp, vertical = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(32.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(HathorColors.AccentFill),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    group.icon,
                    contentDescription = null,
                    tint = HathorColors.AccentBright,
                    modifier = Modifier.size(18.dp)
                )
            }
            Text(
                group.title,
                style = MaterialTheme.typography.titleSmall,
                modifier = Modifier.weight(1f)
            )
            Icon(
                if (expanded) Icons.Filled.ExpandLess else Icons.Filled.ExpandMore,
                contentDescription = if (expanded) "Collapse ${group.title}" else "Expand ${group.title}",
                tint = HathorColors.TextHint
            )
        }
        AnimatedVisibility(
            visible = expanded,
            enter = expandVertically(animationSpec = tween(200)) +
                fadeIn(animationSpec = tween(200)),
            exit = shrinkVertically(animationSpec = tween(180)) +
                fadeOut(animationSpec = tween(120))
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = 14.dp, end = 14.dp, bottom = 14.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
                content = content
            )
        }
    }
}

@Composable
private fun GroupDivider() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(1.dp)
            .background(HathorColors.BorderFaint)
    )
}

@Composable
private fun SectionLabel(icon: ImageVector, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Icon(icon, contentDescription = null, tint = HathorColors.AccentBright)
        Text(text, style = MaterialTheme.typography.titleSmall)
    }
}

private fun updateStatusText(state: UpdateState): String = when (state) {
    UpdateState.Idle -> ""
    UpdateState.Disabled -> "Configure Web API to check for updates."
    UpdateState.Checking -> "Checking…"
    UpdateState.UpToDate -> "Up to date."
    UpdateState.NoRelease -> "No APK published yet."
    is UpdateState.Available ->
        "Update available: ${state.info.versionName} (build ${state.info.versionCode})."
    is UpdateState.Downloading -> {
        val total = state.total.coerceAtLeast(1L)
        "Downloading… ${((state.bytes * 100) / total).coerceIn(0L, 100L)}%"
    }
    is UpdateState.Ready -> "Update ready: ${state.info.versionName}."
    is UpdateState.NeedsPermission -> "Allow installs from this source, then return."
    is UpdateState.Error -> state.message
    is UpdateState.Installing -> "Installer opened."
}
