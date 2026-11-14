package com.musicplayer.android.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Error
import androidx.compose.material.icons.filled.Lyrics
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
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
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.musicplayer.android.data.LyricSuggestion
import com.musicplayer.android.data.LyricsRepository
import com.musicplayer.android.data.LyricsRomanizer
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PodcastChapter
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.data.splitLyricsChapter
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.ModernSlider
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.roundToInt

private enum class LyricsMode { View, Search, Edit }

private const val OFFSET_STEP_MS = 250
private const val OFFSET_LIMIT_MS = 20_000

/**
 * Full-screen lyrics experience (web NowPlaying/LyricsSheet parity): large
 * centered lines, tap-to-seek, active-line follow, offset correction, romaji,
 * search/replace and manual editing. Deliberately not a bottom sheet.
 */
@Composable
fun LyricsScreen(
    file: File,
    metadata: MetadataRepository,
    lyrics: LyricsRepository,
    player: PlayerManager,
    chaptersProvider: (suspend (String) -> List<PodcastChapter>)?,
    isPodcast: Boolean,
    onDismiss: () -> Unit
) {
    var meta by remember(file.absolutePath) { mutableStateOf<SongMeta?>(null) }
    LaunchedEffect(file.absolutePath) {
        meta = withContext(Dispatchers.IO) {
            try {
                metadata.metaFor(file)
            } catch (_: Exception) {
                null
            }
        }
    }
    val currentMeta = meta
    if (currentMeta == null) {
        Box(
            modifier = Modifier.fillMaxSize().background(HathorColors.Background),
            contentAlignment = Alignment.Center
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator(color = HathorColors.AccentBright, strokeWidth = 2.dp)
                Spacer(Modifier.height(10.dp))
                Text("Loading lyrics…", color = HathorColors.TextHint)
            }
        }
        BackHandler(onBack = onDismiss)
        return
    }
    LyricsContent(
        file = file,
        meta = currentMeta,
        lyrics = lyrics,
        player = player,
        chaptersProvider = chaptersProvider,
        isPodcast = isPodcast,
        onDismiss = onDismiss
    )
}

@Composable
private fun LyricsContent(
    file: File,
    meta: SongMeta,
    lyrics: LyricsRepository,
    player: PlayerManager,
    chaptersProvider: (suspend (String) -> List<PodcastChapter>)?,
    isPodcast: Boolean,
    onDismiss: () -> Unit
) {
    val state by lyrics.state.collectAsState()
    var mode by remember(file.absolutePath) { mutableStateOf(LyricsMode.View) }
    var chapters by remember(file.absolutePath) { mutableStateOf<List<PodcastChapter>>(emptyList()) }
    var selectedChapter by remember(file.absolutePath) { mutableStateOf<PodcastChapter?>(null) }
    var searchTrack by remember(file.absolutePath) { mutableStateOf(meta.title) }
    var searchArtist by remember(file.absolutePath) { mutableStateOf(meta.artist) }
    var suggestions by remember { mutableStateOf<List<LyricSuggestion>>(emptyList()) }
    var searching by remember { mutableStateOf(false) }
    var editingText by remember { mutableStateOf<String?>(null) }
    var savingId by remember { mutableStateOf<Long?>(null) }
    var previewId by remember { mutableStateOf<Long?>(null) }
    var saveMsg by remember { mutableStateOf<String?>(null) }
    var offsetMs by remember(file.absolutePath) { mutableStateOf(0) }
    var romajiOn by remember(file.absolutePath) { mutableStateOf(false) }
    var romanizedSynced by remember { mutableStateOf<String?>(null) }
    var romanizedPlain by remember { mutableStateOf<String?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }
    var autoSearchKey by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    val lyricsKey = selectedChapter?.let { "${file.name}::chapter:${it.id}" } ?: file.name
    val chapterSplit = selectedChapter?.let { splitLyricsChapter(it.name) }
    val trackTitle = chapterSplit?.first ?: meta.title
    val trackArtist = chapterSplit?.second ?: meta.artist
    val trackAlbum = if (selectedChapter != null) "" else meta.album
    val trackDuration = selectedChapter?.let { chapter ->
        val end = chapter.endSecs
        if (end != null && end > chapter.startSecs) (end - chapter.startSecs).roundToInt()
        else meta.durationSec.toInt()
    } ?: meta.durationSec.toInt()
    val timelineStartMs = ((selectedChapter?.startSecs ?: 0.0) * 1000).roundToInt()

    LaunchedEffect(file.absolutePath) {
        if (isPodcast && chaptersProvider != null) {
            chapters = try {
                chaptersProvider(file.name)
            } catch (_: Exception) {
                emptyList()
            }
        }
    }

    LaunchedEffect(lyricsKey) {
        lyrics.reset()
        mode = LyricsMode.View
        suggestions = emptyList()
        editingText = null
        saveMsg = null
        romanizedSynced = null
        romanizedPlain = null
        autoSearchKey = null
        lyrics.loadFor(lyricsKey, trackTitle, trackArtist, trackAlbum, trackDuration)
        offsetMs = lyrics.loadOffset(lyricsKey)
    }

    LaunchedEffect(romajiOn, state) {
        val ready = state as? LyricsRepository.LyricsState.Ready
        if (!romajiOn || ready == null) {
            romanizedSynced = null
            romanizedPlain = null
            return@LaunchedEffect
        }
        withContext(Dispatchers.Default) {
            romanizedSynced = ready.result.synced?.let { LyricsRomanizer.romanize(it, isLrc = true) }
            romanizedPlain = ready.result.plain?.let { LyricsRomanizer.romanize(it, isLrc = false) }
        }
    }

    val ready = state as? LyricsRepository.LyricsState.Ready
    val displayState = if (romajiOn && ready != null) {
        val synced = romanizedSynced ?: ready.result.synced
        val plain = romanizedPlain ?: ready.result.plain
        LyricsRepository.LyricsState.Ready(
            ready.result.copy(synced = synced, plain = plain, displayText = plain ?: synced)
        )
    } else {
        state
    }
    val hasLyrics = ready?.result?.let {
        !it.synced.isNullOrBlank() || !it.plain.isNullOrBlank()
    } == true
    val hasSynced = ready?.result?.synced?.isNotBlank() == true

    fun selectChapter(chapter: PodcastChapter?) {
        selectedChapter = chapter
        val split = chapter?.let { splitLyricsChapter(it.name) }
        searchTrack = split?.first ?: meta.title
        searchArtist = split?.second ?: meta.artist
        suggestions = emptyList()
        previewId = null
        autoSearchKey = null
    }

    fun changeOffset(delta: Int) {
        val next = (offsetMs + delta).coerceIn(-OFFSET_LIMIT_MS, OFFSET_LIMIT_MS)
        offsetMs = next
        scope.launch { offsetMs = lyrics.saveOffset(lyricsKey, next) }
    }

    fun runSearch() {
        if (searchTrack.isBlank()) {
            saveMsg = "Enter a track name to search."
            return
        }
        searching = true
        saveMsg = null
        suggestions = emptyList()
        scope.launch {
            suggestions = lyrics.searchSuggestions(
                searchTrack.trim(),
                searchArtist.trim(),
                trackAlbum.takeIf { it.isNotBlank() && it != "Unknown" } ?: "",
                trackDuration
            )
            searching = false
            if (suggestions.isEmpty()) {
                saveMsg = "No candidates — try different spelling or remove the artist."
            }
        }
    }

    fun saveSuggestion(suggestion: LyricSuggestion) {
        savingId = suggestion.id
        saveMsg = null
        scope.launch {
            if (lyrics.saveLyrics(lyricsKey, suggestion.plainLyrics, suggestion.syncedLyrics)) {
                lyrics.loadFor(lyricsKey, trackTitle, trackArtist, trackAlbum, trackDuration)
                mode = LyricsMode.View
                previewId = null
            } else {
                saveMsg = "Save failed."
            }
            savingId = null
        }
    }

    BackHandler {
        if (mode != LyricsMode.View) mode = LyricsMode.View else onDismiss()
    }

    // Opening Search preloads candidates with the song's own metadata (web).
    LaunchedEffect(mode, lyricsKey) {
        if (mode == LyricsMode.Search) {
            val key = "$lyricsKey|$searchTrack|$searchArtist"
            if (autoSearchKey != key) {
                autoSearchKey = key
                runSearch()
            }
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(HathorColors.Background)
            .statusBarsPadding()
            .navigationBarsPadding()
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 6.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(2.dp)
        ) {
            IconButton(onClick = {
                if (mode != LyricsMode.View) mode = LyricsMode.View else onDismiss()
            }) {
                Icon(
                    Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = "Back",
                    tint = HathorColors.TextBody
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    meta.title.ifBlank { file.nameWithoutExtension },
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    listOfNotNull(
                        meta.artist.takeIf { it.isNotBlank() && it != "Unknown" },
                        meta.album.takeIf { it.isNotBlank() && it != "Unknown" }
                    ).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            if (mode == LyricsMode.View) {
                TextButton(onClick = { mode = LyricsMode.Search }) {
                    Text(
                        if (hasLyrics) "Replace" else "Search",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.AccentBright
                    )
                }
                IconButton(onClick = {
                    editingText = ready?.result?.displayText
                        ?: ready?.result?.plain
                        ?: ready?.result?.synced
                        ?: ""
                    mode = LyricsMode.Edit
                }) {
                    Icon(
                        Icons.Filled.Edit,
                        contentDescription = "Edit lyrics",
                        tint = HathorColors.TextHint
                    )
                }
            } else {
                TextButton(onClick = { mode = LyricsMode.View }) {
                    Text("Done", style = MaterialTheme.typography.bodySmall)
                }
            }
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 12.dp, vertical = 2.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Row(
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(Color.White.copy(alpha = 0.05f))
                    .border(
                        1.dp,
                        Color.White.copy(alpha = 0.08f),
                        RoundedCornerShape(50)
                    )
                    .padding(horizontal = 2.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(
                    onClick = { changeOffset(-OFFSET_STEP_MS) },
                    enabled = hasSynced,
                    modifier = Modifier.size(32.dp)
                ) {
                    Icon(
                        Icons.Filled.Remove,
                        contentDescription = "Earlier lyrics",
                        tint = HathorColors.TextHint,
                        modifier = Modifier.size(16.dp)
                    )
                }
                Text(
                    text = (if (offsetMs > 0) "+" else "") + "$offsetMs ms",
                    style = TextStyle(
                        fontFamily = FontFamily.Monospace,
                        fontSize = 11.sp,
                        color = if (offsetMs != 0) HathorColors.AccentBright else HathorColors.TextHint
                    ),
                    modifier = Modifier.clickable(enabled = offsetMs != 0) {
                        changeOffset(-offsetMs)
                    }
                )
                IconButton(
                    onClick = { changeOffset(OFFSET_STEP_MS) },
                    enabled = hasSynced,
                    modifier = Modifier.size(32.dp)
                ) {
                    Icon(
                        Icons.Filled.Add,
                        contentDescription = "Later lyrics",
                        tint = HathorColors.TextHint,
                        modifier = Modifier.size(16.dp)
                    )
                }
            }
            TogglePill(
                label = "Romaji",
                selected = romajiOn,
                enabled = hasLyrics
            ) { romajiOn = !romajiOn }
            if (ready?.result?.fromCache == true) {
                Text(
                    "CACHED",
                    style = TextStyle(
                        fontFamily = FontFamily.Monospace,
                        fontSize = 9.sp,
                        color = HathorColors.TextHint.copy(alpha = 0.7f)
                    )
                )
            }
        }

        Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
            when (mode) {
                LyricsMode.View -> LyricsBody(
                    state = displayState,
                    player = player,
                    offsetMs = offsetMs,
                    timelineStartMs = timelineStartMs,
                    onSearch = { mode = LyricsMode.Search },
                    onRetry = {
                        scope.launch {
                            lyrics.loadFor(lyricsKey, trackTitle, trackArtist, trackAlbum, trackDuration)
                        }
                    }
                )
                LyricsMode.Search -> LyricsSearchPanel(
                    track = searchTrack,
                    artist = searchArtist,
                    onTrack = {
                        searchTrack = it
                        autoSearchKey = null
                    },
                    onArtist = {
                        searchArtist = it
                        autoSearchKey = null
                    },
                    searching = searching,
                    suggestions = suggestions,
                    saveMsg = saveMsg,
                    previewId = previewId,
                    savingId = savingId,
                    hasLyrics = hasLyrics,
                    chapters = chapters,
                    selectedChapter = selectedChapter,
                    onSelectChapter = ::selectChapter,
                    onSearch = ::runSearch,
                    onPreview = { previewId = if (previewId == it) null else it },
                    onSave = ::saveSuggestion,
                    onDelete = { confirmDelete = true },
                    onClose = { mode = LyricsMode.View }
                )
                LyricsMode.Edit -> LyricsEditPanel(
                    text = editingText ?: "",
                    onText = { editingText = it },
                    saveMsg = saveMsg,
                    onSave = {
                        scope.launch {
                            saveMsg = if (lyrics.saveLyrics(lyricsKey, editingText, null)) {
                                lyrics.loadFor(lyricsKey, trackTitle, trackArtist, trackAlbum, trackDuration)
                                mode = LyricsMode.View
                                null
                            } else {
                                "Nothing to save."
                            }
                        }
                    },
                    onCancel = { mode = LyricsMode.View }
                )
            }
        }

        if (mode == LyricsMode.View) {
            LyricsTransport(player)
        }
    }

    if (confirmDelete) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete lyrics?") },
            text = {
                Text(
                    "Remove the cached lyrics for " +
                        (selectedChapter?.name ?: meta.title.ifBlank { file.name }) +
                        "? You can search for them again later."
                )
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch {
                            lyrics.deleteLyrics(lyricsKey)
                            confirmDelete = false
                            mode = LyricsMode.View
                            romajiOn = false
                            offsetMs = 0
                            lyrics.loadFor(
                                lyricsKey, trackTitle, trackArtist, trackAlbum, trackDuration
                            )
                        }
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.error
                    )
                ) { Text("Delete") }
            },
            dismissButton = {
                TextButton(onClick = { confirmDelete = false }) { Text("Cancel") }
            }
        )
    }
}

@Composable
private fun TogglePill(
    label: String,
    selected: Boolean,
    enabled: Boolean,
    onClick: () -> Unit
) {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(
                if (selected) HathorColors.AccentFill else Color.White.copy(alpha = 0.05f)
            )
            .border(
                1.dp,
                if (selected) HathorColors.Accent.copy(alpha = 0.45f)
                else Color.White.copy(alpha = 0.08f),
                RoundedCornerShape(50)
            )
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 13.dp, vertical = 7.dp)
    ) {
        Text(
            label,
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 9.sp,
                letterSpacing = 1.4.sp
            ),
            color = when {
                !enabled -> HathorColors.TextHint.copy(alpha = 0.5f)
                selected -> HathorColors.AccentBright
                else -> HathorColors.TextHint
            }
        )
    }
}

@Composable
private fun LyricsBody(
    state: LyricsRepository.LyricsState,
    player: PlayerManager,
    offsetMs: Int,
    timelineStartMs: Int,
    onSearch: () -> Unit,
    onRetry: () -> Unit
) {
    when (state) {
        is LyricsRepository.LyricsState.Idle,
        is LyricsRepository.LyricsState.Loading -> Box(
            modifier = Modifier.fillMaxSize(),
            contentAlignment = Alignment.Center
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator(color = HathorColors.AccentBright, strokeWidth = 2.dp)
                Spacer(Modifier.height(10.dp))
                Text("Loading lyrics…", color = HathorColors.TextHint)
            }
        }
        is LyricsRepository.LyricsState.Ready -> {
            val synced = state.result.synced?.takeIf { it.isNotBlank() }
            val plain = state.result.plain?.takeIf { it.isNotBlank() }
            when {
                synced != null -> FullSyncedLyrics(
                    lines = remember(synced) { parseLrc(synced) },
                    player = player,
                    offsetMs = offsetMs,
                    timelineStartMs = timelineStartMs
                )
                plain != null -> PlainLyrics(plain)
                else -> EmptyLyrics(onSearch = onSearch, onRetry = onRetry)
            }
        }
        is LyricsRepository.LyricsState.Failed -> LyricsError(
            message = state.message,
            onSearch = onSearch,
            onRetry = onRetry
        )
    }
}

@Composable
private fun EmptyLyrics(onSearch: () -> Unit, onRetry: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp),
            modifier = Modifier.padding(32.dp)
        ) {
            Icon(
                Icons.Filled.Lyrics,
                contentDescription = null,
                tint = HathorColors.TextHint,
                modifier = Modifier.size(48.dp)
            )
            Text(
                "No lyrics found.",
                style = MaterialTheme.typography.headlineSmall,
                color = HathorColors.TextBody
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = onSearch,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White
                    )
                ) { Text("Search lyrics") }
                OutlinedButton(onClick = onRetry) { Text("Retry") }
            }
        }
    }
}

@Composable
private fun LyricsError(message: String, onSearch: () -> Unit, onRetry: () -> Unit) {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp),
            modifier = Modifier.padding(32.dp)
        ) {
            Icon(
                Icons.Filled.Error,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.error,
                modifier = Modifier.size(40.dp)
            )
            Text(
                message,
                style = MaterialTheme.typography.bodyMedium,
                color = HathorColors.TextBody,
                textAlign = TextAlign.Center
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = onRetry) { Text("Retry") }
                OutlinedButton(onClick = onSearch) { Text("Search lyrics") }
            }
        }
    }
}

@Composable
private fun PlainLyrics(plain: String) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 28.dp, vertical = 20.dp)
    ) {
        Text(
            plain,
            style = MaterialTheme.typography.bodyLarge.copy(
                fontSize = 18.sp,
                lineHeight = 30.sp,
                color = HathorColors.TextBody
            ),
            textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(Modifier.height(80.dp))
    }
}

/**
 * Web SyncedLyrics equivalent: giant centered lines, active line in white and
 * gently scaled, tap-to-seek, and smooth centering of the active line.
 */
@Composable
private fun FullSyncedLyrics(
    lines: List<LrcLine>,
    player: PlayerManager,
    offsetMs: Int,
    timelineStartMs: Int
) {
    if (lines.isEmpty()) {
        Text(
            "No synchronized lyrics found.",
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.fillMaxSize().padding(32.dp),
            textAlign = TextAlign.Center
        )
        return
    }
    val position by player.positionMs.collectAsState()
    val nowSec = (position - timelineStartMs) / 1000.0 + offsetMs / 1000.0
    var active = -1
    for (i in lines.indices) {
        if (nowSec >= lines[i].timeSec) active = i else break
    }
    val listState = rememberLazyListState()
    LaunchedEffect(active, lines.size) {
        if (active >= 0) {
            try {
                listState.animateScrollToItem(active)
            } catch (_: Exception) {
            }
        }
    }

    BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
        val edge = (maxHeight / 2) - 34.dp
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(top = edge, bottom = edge),
            verticalArrangement = Arrangement.spacedBy(2.dp)
        ) {
            itemsIndexed(lines, key = { i, line -> "$i-${line.timeSec}" }) { i, line ->
                val isActive = i == active
                val color by animateColorAsState(
                    if (isActive) Color.White else Color(0xFF52525B),
                    label = "lyric-color"
                )
                val scale by animateFloatAsState(
                    if (isActive) 1.03f else 1f,
                    label = "lyric-scale"
                )
                Text(
                    line.text,
                    style = MaterialTheme.typography.headlineMedium.copy(
                        fontSize = if (isActive) 30.sp else 24.sp,
                        lineHeight = if (isActive) 38.sp else 31.sp,
                        fontWeight = FontWeight.Bold,
                        color = color
                    ),
                    textAlign = TextAlign.Center,
                    modifier = Modifier
                        .fillMaxWidth()
                        .graphicsLayer {
                            scaleX = scale
                            scaleY = scale
                        }
                        .clickable {
                            player.seekTo(
                                (timelineStartMs + line.timeSec * 1000 - offsetMs)
                                    .roundToInt()
                            )
                        }
                        .padding(horizontal = 26.dp, vertical = 9.dp)
                )
            }
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(68.dp)
                .align(Alignment.TopCenter)
                .background(
                    Brush.verticalGradient(
                        listOf(HathorColors.Background, HathorColors.Background.copy(alpha = 0f))
                    )
                )
        )
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(68.dp)
                .align(Alignment.BottomCenter)
                .background(
                    Brush.verticalGradient(
                        listOf(HathorColors.Background.copy(alpha = 0f), HathorColors.Background)
                    )
                )
        )
    }
}

@Composable
private fun LyricsSearchPanel(
    track: String,
    artist: String,
    onTrack: (String) -> Unit,
    onArtist: (String) -> Unit,
    searching: Boolean,
    suggestions: List<LyricSuggestion>,
    saveMsg: String?,
    previewId: Long?,
    savingId: Long?,
    hasLyrics: Boolean,
    chapters: List<PodcastChapter>,
    selectedChapter: PodcastChapter?,
    onSelectChapter: (PodcastChapter?) -> Unit,
    onSearch: () -> Unit,
    onPreview: (Long) -> Unit,
    onSave: (LyricSuggestion) -> Unit,
    onDelete: () -> Unit,
    onClose: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(horizontal = 20.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text(
            "FIND LYRICS",
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                letterSpacing = 2.0.sp,
                color = HathorColors.TextHint
            )
        )
        if (chapters.isNotEmpty()) {
            Text(
                "Save to chapter",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                FilterChip(
                    selected = selectedChapter == null,
                    onClick = { onSelectChapter(null) },
                    label = { Text("Episode") }
                )
                chapters.forEach { chapter ->
                    FilterChip(
                        selected = selectedChapter?.id == chapter.id,
                        onClick = { onSelectChapter(chapter) },
                        label = { Text(chapter.name, maxLines = 1) }
                    )
                }
            }
        }
        HathorTextField(
            value = track,
            onValueChange = onTrack,
            label = "Track name",
            modifier = Modifier.fillMaxWidth()
        )
        HathorTextField(
            value = artist,
            onValueChange = onArtist,
            label = "Artist (optional)",
            modifier = Modifier.fillMaxWidth()
        )
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = onSearch,
                enabled = !searching && track.isNotBlank(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) {
                if (searching) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(14.dp),
                        strokeWidth = 2.dp,
                        color = Color.White
                    )
                    Spacer(Modifier.width(6.dp))
                } else {
                    Icon(
                        Icons.Filled.Search,
                        contentDescription = null,
                        modifier = Modifier.size(16.dp)
                    )
                    Spacer(Modifier.width(6.dp))
                }
                Text(if (searching) "Searching" else "Search")
            }
            if (hasLyrics) {
                OutlinedButton(onClick = onDelete) {
                    Icon(
                        Icons.Filled.Delete,
                        contentDescription = null,
                        modifier = Modifier.size(16.dp),
                        tint = MaterialTheme.colorScheme.error
                    )
                    Spacer(Modifier.width(6.dp))
                    Text("Remove saved", color = MaterialTheme.colorScheme.error)
                }
            }
            Spacer(Modifier.weight(1f))
            TextButton(onClick = onClose) { Text("Close") }
        }
        if (searching) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .hathorGlass()
                    .padding(horizontal = 14.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    strokeWidth = 2.dp,
                    color = HathorColors.AccentBright
                )
                Column {
                    Text(
                        "Searching lyrics…",
                        style = MaterialTheme.typography.titleSmall,
                        color = HathorColors.TextPrimary
                    )
                    Text(
                        "Using the same Hathor server source and filters as the web.",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint
                    )
                }
            }
        }
        saveMsg?.let {
            Text(
                it,
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.AccentBright,
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(10.dp))
                    .background(HathorColors.AccentFill)
                    .padding(horizontal = 10.dp, vertical = 8.dp)
            )
        }
        suggestions.forEach { suggestion ->
            val expanded = previewId == suggestion.id
            Column(
                modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Text(
                    "${suggestion.trackName} — ${suggestion.artistName}",
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    buildString {
                        append(if (suggestion.syncedLyrics != null) "Synced" else "Plain")
                        if (suggestion.albumName.isNotBlank()) append(" · ${suggestion.albumName}")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    TextButton(onClick = { onPreview(suggestion.id) }) {
                        Text(if (expanded) "Hide" else "Preview")
                    }
                    Spacer(Modifier.weight(1f))
                    Button(
                        onClick = { onSave(suggestion) },
                        enabled = savingId == null,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = HathorColors.Accent,
                            contentColor = Color.White
                        )
                    ) {
                        if (savingId == suggestion.id) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(14.dp),
                                strokeWidth = 2.dp,
                                color = Color.White
                            )
                            Spacer(Modifier.width(6.dp))
                        }
                        Text(if (hasLyrics) "Replace" else "Save")
                    }
                }
                if (expanded) {
                    Text(
                        suggestion.syncedLyrics ?: suggestion.plainLyrics ?: "(empty)",
                        style = MaterialTheme.typography.bodySmall.copy(
                            fontFamily = FontFamily.Monospace,
                            lineHeight = 18.sp
                        ),
                        color = HathorColors.TextBody,
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(max = 220.dp)
                            .verticalScroll(rememberScrollState())
                            .background(Color.Black.copy(alpha = 0.25f), RoundedCornerShape(8.dp))
                            .padding(10.dp)
                    )
                }
            }
        }
        Spacer(Modifier.height(40.dp))
    }
}

@Composable
private fun LyricsEditPanel(
    text: String,
    onText: (String) -> Unit,
    saveMsg: String?,
    onSave: () -> Unit,
    onCancel: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        Text(
            "EDIT LYRICS",
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                letterSpacing = 2.0.sp,
                color = HathorColors.TextHint
            )
        )
        Text(
            "Saving here stores plain lyrics. Search for a synced candidate to keep timing.",
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint
        )
        HathorTextField(
            value = text,
            onValueChange = onText,
            singleLine = false,
            minLines = 14,
            maxLines = 30,
            modifier = Modifier.fillMaxWidth().heightIn(min = 300.dp)
        )
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = onSave,
                enabled = text.isNotBlank(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) {
                Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(6.dp))
                Text("Save")
            }
            OutlinedButton(onClick = onCancel) { Text("Cancel") }
            Spacer(Modifier.weight(1f))
            Text(
                "${text.lines().size} lines",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
        }
        saveMsg?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
        Spacer(Modifier.height(40.dp))
    }
}

@Composable
private fun LyricsTransport(player: PlayerManager) {
    val position by player.positionMs.collectAsState()
    val duration by player.durationMs.collectAsState()
    val playing by player.playing.collectAsState()
    val current by player.current.collectAsState()
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(Color(0x66000000))
            .padding(horizontal = 10.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(2.dp)
    ) {
        Text(
            fmtTime(position),
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint,
            modifier = Modifier.width(36.dp)
        )
        ModernSlider(
            value = if (duration > 0) position.toFloat() / duration.toFloat() else 0f,
            onValueChange = { if (duration > 0) player.seekTo((it * duration).toInt()) },
            enabled = current != null && duration > 0,
            modifier = Modifier.weight(1f)
        )
        Text(
            fmtTime(duration),
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint,
            modifier = Modifier.width(36.dp)
        )
        IconButton(onClick = { player.prev() }, enabled = current != null) {
            Icon(
                Icons.Filled.SkipPrevious,
                contentDescription = "Previous",
                tint = HathorColors.TextBody,
                modifier = Modifier.size(22.dp)
            )
        }
        IconButton(onClick = { player.toggle() }, enabled = current != null) {
            Icon(
                if (playing) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                contentDescription = if (playing) "Pause" else "Play",
                tint = HathorColors.TextBody,
                modifier = Modifier.size(26.dp)
            )
        }
        IconButton(onClick = { player.next() }, enabled = current != null) {
            Icon(
                Icons.Filled.SkipNext,
                contentDescription = "Next",
                tint = HathorColors.TextBody,
                modifier = Modifier.size(22.dp)
            )
        }
    }
}

private fun fmtTime(ms: Int): String {
    if (ms <= 0) return "0:00"
    val s = ms / 1000
    return "%d:%02d".format(s / 60, s % 60)
}

data class LrcLine(val timeSec: Double, val text: String)

/**
 * Desktop parseLRC(): first [mm:ss.xx] tag per line, empty text -> ♪,
 * untagged lines skipped. Internal so the JVM tests can pin the format.
 */
internal fun parseLrc(lrc: String): List<LrcLine> {
    val tag = Regex("\\[(\\d{2}):(\\d{2})\\.(\\d{2,3})\\]")
    val out = mutableListOf<LrcLine>()
    for (line in lrc.lines()) {
        val m = tag.find(line) ?: continue
        val min = m.groupValues[1].toIntOrNull() ?: continue
        val sec = m.groupValues[2].toIntOrNull() ?: continue
        val ms = m.groupValues[3].padEnd(3, '0').toIntOrNull() ?: 0
        val text = tag.replace(line, "").trim().ifEmpty { "♪" }
        out.add(LrcLine(min * 60 + sec + ms / 1000.0, text))
    }
    return out
}
