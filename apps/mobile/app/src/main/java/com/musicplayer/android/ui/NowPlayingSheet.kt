@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
package com.musicplayer.android.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Error
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.RepeatOne
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedIconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
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
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.LyricSuggestion
import com.musicplayer.android.data.LyricsRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.ModernSlider
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.File

/**
 * NowPlaying sheet (desktop playing view + queue sidebar + lyrics overlay):
 * big cover, tags, seek, controls, expandable queue with jump-to, and a
 * lyrics sheet (lrclib, cached in Room).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NowPlayingSheet(
    player: PlayerManager,
    metadata: MetadataRepository,
    lyrics: LyricsRepository,
    volume: Float,
    onVolumeChange: (Float) -> Unit,
    onVolumeDone: () -> Unit,
    onDismiss: () -> Unit
) {
    val current by player.current.collectAsState()
    val playing by player.playing.collectAsState()
    val position by player.positionMs.collectAsState()
    val duration by player.durationMs.collectAsState()
    val queue by player.queueFlow.collectAsState()
    val index by player.indexFlow.collectAsState()
    val shuffled by player.shuffleFlow.collectAsState()
    val repeatOne by player.repeatFlow.collectAsState()

    var meta by remember { mutableStateOf<SongMeta?>(null) }
    var cover by remember { mutableStateOf<android.graphics.Bitmap?>(null) }
    var showQueue by remember { mutableStateOf(false) }
    var showLyrics by remember { mutableStateOf(false) }
    var queueTitles by remember { mutableStateOf<Map<String, String>>(emptyMap()) }
    var queueCovers by remember { mutableStateOf<Map<String, android.graphics.Bitmap?>>(emptyMap()) }
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current

    LaunchedEffect(current) {
        val f = current
        if (f == null) {
            meta = null
            cover = null
        } else {
            scope.launch(Dispatchers.IO) {
                // Cache-first: the queue sweep below used to re-open every
                // file with MediaMetadataRetriever on each queue change.
                // File-based cover resolves each item to the correct folder.
                val m = metadata.metaFor(f)
                val px = with(density) { 240.dp.toPx() }.toInt() * 2
                val pic = metadata.coverArtFor(f, px)
                meta = m
                cover = pic
            }
        }
    }
    LaunchedEffect(queue) {
        scope.launch(Dispatchers.IO) {
            queueTitles = queue.associate { it.absolutePath to metadata.metaFor(it).title }
            val thumbPx = with(density) { 40.dp.toPx() }.toInt() * 2
            queueCovers = queue.associate { f ->
                f.absolutePath to metadata.coverArtFor(f, thumbPx)
            }
        }
    }
    // Queue-item context menu (long-press opens the same actions as the row).
    var queueMenuIndex by remember { mutableStateOf<Int?>(null) }

    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Box(
                modifier = Modifier.size(240.dp)
                    .shadow(
                        elevation = 24.dp,
                        shape = RoundedCornerShape(24.dp),
                        spotColor = HathorColors.Accent.copy(alpha = 0.45f)
                    )
                    .clip(RoundedCornerShape(24.dp))
                    .background(HathorColors.AccentFill),
                contentAlignment = Alignment.Center
            ) {
                val bmp = cover
                if (bmp != null) {
                    Image(bitmap = bmp.asImageBitmap(), contentDescription = "Cover", modifier = Modifier.size(240.dp))
                } else {
                    Icon(
                        Icons.Filled.MusicNote,
                        contentDescription = null,
                        tint = HathorColors.AccentBright,
                        modifier = Modifier.size(72.dp)
                    )
                }
            }
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(meta?.title ?: current?.nameWithoutExtension ?: "Nothing playing", style = MaterialTheme.typography.titleLarge)
                Text(
                    listOfNotNull(meta?.artist, meta?.album).joinToString(" · ").ifBlank { "" },
                    style = MaterialTheme.typography.bodyMedium,
                    color = HathorColors.TextHint
                )
            }
            Column {
                ModernSlider(
                    value = if (duration > 0) position.toFloat() / duration.toFloat() else 0f,
                    onValueChange = { if (duration > 0) player.seekTo((it * duration).toInt()) },
                    enabled = current != null && duration > 0,
                    modifier = Modifier.fillMaxWidth()
                )
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(formatMs(position), style = MaterialTheme.typography.bodySmall)
                    Text(formatMs(duration), style = MaterialTheme.typography.bodySmall)
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { player.toggleShuffle() }) {
                    Icon(
                        Icons.Filled.Shuffle,
                        contentDescription = "Shuffle",
                        tint = if (shuffled) HathorColors.AccentBright else HathorColors.TextHint
                    )
                }
                IconButton(onClick = { player.prev() }, enabled = current != null) {
                    Icon(Icons.Filled.SkipPrevious, contentDescription = "Previous", tint = HathorColors.TextPrimary, modifier = Modifier.size(36.dp))
                }
                // Accent play button: orange gradient circle, white glyph.
                Box(
                    modifier = Modifier.size(68.dp)
                        .clip(CircleShape)
                        .background(
                            Brush.linearGradient(
                                listOf(HathorColors.AccentBright, HathorColors.AccentDeep)
                            )
                        )
                        .clickable { player.toggle() },
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        if (playing) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                        contentDescription = if (playing) "Pause" else "Play",
                        tint = Color.White,
                        modifier = Modifier.size(36.dp)
                    )
                }
                IconButton(onClick = { player.next() }, enabled = current != null) {
                    Icon(Icons.Filled.SkipNext, contentDescription = "Next", tint = HathorColors.TextPrimary, modifier = Modifier.size(36.dp))
                }
                IconButton(onClick = { player.toggleRepeat() }) {
                    Icon(
                        if (repeatOne) Icons.Filled.RepeatOne else Icons.Filled.Repeat,
                        contentDescription = "Repeat",
                        tint = if (repeatOne) HathorColors.AccentBright else HathorColors.TextHint
                    )
                }
            }
            // Mobile adaptation: volume lives on the song screen, not a separate one.
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
                Icon(Icons.Filled.VolumeUp, contentDescription = "Volume", tint = HathorColors.TextHint, modifier = Modifier.size(20.dp))
                ModernSlider(
                    value = volume,
                    onValueChange = onVolumeChange,
                    onValueChangeFinished = onVolumeDone,
                    modifier = Modifier.weight(1f)
                )
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = { showQueue = !showQueue }) {
                    Icon(Icons.Filled.List, contentDescription = null, tint = HathorColors.AccentBright)
                    Text(if (showQueue) " Hide queue" else " Queue (${queue.size})")
                }
                TextButton(onClick = { showLyrics = true }, enabled = current != null) {
                    Icon(Icons.Filled.Mic, contentDescription = null, tint = HathorColors.AccentBright)
                    Text(" Lyrics")
                }
            }
            if (showQueue) {
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(
                        onClick = { player.clearUpcoming() },
                        enabled = queue.size > 1
                    ) { Text("Clear upcoming") }
                }
            }
            AnimatedVisibility(
                // Grow upward so the list appears above the buttons instead
                // of pushing below the visible sheet area.
                visible = showQueue,
                enter = expandVertically(expandFrom = Alignment.Bottom, animationSpec = tween(300)) +
                    fadeIn(animationSpec = tween(300)),
                exit = shrinkVertically(shrinkTowards = Alignment.Bottom, animationSpec = tween(300)) +
                    fadeOut(animationSpec = tween(300))
            ) {
                LazyColumn(modifier = Modifier.fillMaxWidth().heightIn(max = 300.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    itemsIndexed(queue, key = { _, f -> f.absolutePath }) { i, f ->
                        Row(
                            modifier = Modifier.fillMaxWidth().animateItemPlacement()
                                .combinedClickable(
                                    onClick = { player.jumpTo(i) },
                                    onLongClick = { queueMenuIndex = i }
                                )
                                .padding(vertical = 6.dp),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            val qCover = queueCovers[f.absolutePath]
                            if (qCover != null) {
                                Image(
                                    bitmap = qCover.asImageBitmap(),
                                    contentDescription = null,
                                    modifier = Modifier.size(40.dp).clip(RoundedCornerShape(8.dp))
                                )
                            } else {
                                Icon(
                                    Icons.Filled.MusicNote,
                                    contentDescription = null,
                                    tint = HathorColors.TextHint,
                                    modifier = Modifier.size(24.dp)
                                )
                            }
                            Text(
                                queueTitles[f.absolutePath] ?: f.nameWithoutExtension,
                                style = MaterialTheme.typography.bodyMedium,
                                color = if (i == index) HathorColors.AccentBright else HathorColors.TextBody,
                                maxLines = 1,
                                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f)
                            )
                            if (i == index) {
                                Icon(Icons.Filled.PlayArrow, contentDescription = "Playing", tint = HathorColors.AccentBright)
                            } else {
                                IconButton(onClick = { player.move(i, i - 1) }, enabled = i > 0, modifier = Modifier.size(32.dp)) {
                                    Icon(Icons.Filled.ArrowUpward, contentDescription = "Move up", tint = HathorColors.TextHint)
                                }
                                IconButton(
                                    onClick = { player.move(i, i + 1) },
                                    enabled = i < queue.size - 1,
                                    modifier = Modifier.size(32.dp)
                                ) {
                                    Icon(Icons.Filled.ArrowDownward, contentDescription = "Move down", tint = HathorColors.TextHint)
                                }
                                IconButton(onClick = { player.removeAt(i) }, modifier = Modifier.size(32.dp)) {
                                    Icon(Icons.Filled.Delete, contentDescription = "Remove", tint = HathorColors.TextHint)
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // Queue-item context menu: jump here or remove (current track locked).
    queueMenuIndex?.let { i ->
        val f = queue.getOrNull(i)
        if (f != null) {
            androidx.compose.material3.AlertDialog(
                onDismissRequest = { queueMenuIndex = null },
                title = {
                    DialogTitleBar(
                        title = queueTitles[f.absolutePath] ?: f.nameWithoutExtension,
                        onClose = { queueMenuIndex = null }
                    )
                },
                text = null,
                confirmButton = {
                    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        androidx.compose.material3.OutlinedButton(
                            onClick = { player.jumpTo(i); queueMenuIndex = null },
                            modifier = Modifier.fillMaxWidth()
                        ) { Text("Play from here") }
                        androidx.compose.material3.OutlinedButton(
                            onClick = { player.removeAt(i); queueMenuIndex = null },
                            enabled = i != index,
                            modifier = Modifier.fillMaxWidth()
                        ) { Text("Remove from queue") }
                    }
                }
            )
        }
    }

    if (showLyrics) {
        val f = current
        val m = meta
        if (f != null && m != null) {
            LyricsSheet(lyrics = lyrics, file = f, meta = m, player = player, onDismiss = { showLyrics = false })
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LyricsSheet(
    lyrics: LyricsRepository,
    file: File,
    meta: SongMeta,
    player: PlayerManager,
    onDismiss: () -> Unit
) {
    val state by lyrics.state.collectAsState()
    var mode by remember { mutableStateOf(0) } // 0 view, 1 search, 2 edit
    var searchTrack by remember(file.absolutePath) { mutableStateOf(meta.title) }
    var searchArtist by remember(file.absolutePath) { mutableStateOf(meta.artist) }
    var suggestions by remember { mutableStateOf<List<LyricSuggestion>>(emptyList()) }
    var searching by remember { mutableStateOf(false) }
    var editText by remember { mutableStateOf<String?>(null) }
    var saveMsg by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(file.absolutePath) {
        lyrics.reset()
        mode = 0
        suggestions = emptyList()
        editText = null
        saveMsg = null
        lyrics.loadFor(file.name, meta.title, meta.artist, meta.album, meta.durationSec)
    }
    // Icon mode switch: entering Edit prefills the editor with what's on
    // screen (or empty), same as the old text tab did.
    fun pickMode(i: Int) {
        if (i == 2) {
            val cur = state
            editText = if (cur is LyricsRepository.LyricsState.Ready) {
                cur.result.displayText ?: ""
            } else ""
        }
        mode = i
    }
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(modifier = Modifier.fillMaxWidth().padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            // Header: title + track caption, icon mode switch, close.
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("Lyrics", style = MaterialTheme.typography.titleMedium)
                    Text(
                        listOfNotNull(
                            meta.title.ifBlank { null },
                            meta.artist.takeIf { it.isNotBlank() && it != "Unknown" }
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                }
                LyricsModeButton(icon = Icons.Filled.Visibility, contentDesc = "View lyrics", selected = mode == 0) { pickMode(0) }
                LyricsModeButton(icon = Icons.Filled.Search, contentDesc = "Search lyrics", selected = mode == 1) { pickMode(1) }
                LyricsModeButton(icon = Icons.Filled.Edit, contentDesc = "Edit lyrics", selected = mode == 2) { pickMode(2) }
                IconButton(onClick = onDismiss) {
                    Icon(Icons.Filled.Close, contentDescription = "Close lyrics", tint = HathorColors.TextHint)
                }
            }
            when (mode) {
                0 -> LyricsView(
                    state = state,
                    player = player,
                    onRetry = {
                        scope.launch { lyrics.loadFor(file.name, meta.title, meta.artist, meta.album, meta.durationSec) }
                    }
                )
                1 -> LyricsSearch(
                    track = searchTrack,
                    artist = searchArtist,
                    onTrack = { searchTrack = it },
                    onArtist = { searchArtist = it },
                    searching = searching,
                    suggestions = suggestions,
                    onSearch = {
                        searching = true
                        suggestions = emptyList()
                        scope.launch {
                            suggestions = lyrics.searchSuggestions(
                                searchTrack, searchArtist,
                                meta.album.takeIf { it != "Unknown" } ?: "",
                                meta.durationSec
                            )
                            searching = false
                        }
                    },
                    onPick = { s ->
                        scope.launch {
                            saveMsg = null
                            if (lyrics.saveLyrics(file.name, s.plainLyrics, s.syncedLyrics)) {
                                mode = 0
                            } else {
                                saveMsg = "Save failed."
                            }
                        }
                    }
                )
                else -> LyricsEdit(
                    text = editText ?: "",
                    onText = { editText = it },
                    saveMsg = saveMsg,
                    onSave = {
                        scope.launch {
                            saveMsg = if (lyrics.saveLyrics(file.name, editText, null)) {
                                mode = 0
                                null
                            } else {
                                "Nothing to save."
                            }
                        }
                    }
                )
            }
        }
    }
}

/**
 * Icon mode switch for the lyrics sheet (replaces the old View/Search/Edit
 * text tabs). Selected mode is a filled accent button, the rest outlined.
 */
@Composable
private fun LyricsModeButton(
    icon: ImageVector,
    contentDesc: String,
    selected: Boolean,
    onClick: () -> Unit
) {
    if (selected) {
        FilledTonalIconButton(
            onClick = onClick,
            colors = IconButtonDefaults.filledTonalIconButtonColors(
                containerColor = HathorColors.Accent,
                contentColor = Color.White
            )
        ) { Icon(icon, contentDescription = contentDesc) }
    } else {
        OutlinedIconButton(onClick = onClick) {
            Icon(icon, contentDescription = contentDesc, tint = HathorColors.TextHint)
        }
    }
}

@Composable
private fun LyricsView(
    state: LyricsRepository.LyricsState,
    player: PlayerManager,
    onRetry: () -> Unit
) {
    when (state) {
        is LyricsRepository.LyricsState.Idle,
        is LyricsRepository.LyricsState.Loading -> Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            modifier = Modifier.padding(vertical = 24.dp)
        ) {
            CircularProgressIndicator(
                modifier = Modifier.size(20.dp),
                color = HathorColors.AccentBright,
                strokeWidth = 2.dp
            )
            Text("Loading…", style = MaterialTheme.typography.bodyMedium, color = HathorColors.TextHint)
        }
        is LyricsRepository.LyricsState.Ready -> {
            if (state.result.fromCache) {
                Text("from library cache", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }
            val synced = state.result.synced?.takeIf { it.isNotBlank() }
            if (synced != null) {
                SyncedLyrics(lines = remember(synced) { parseLrc(synced) }, player = player)
            } else {
                Text(
                    state.result.plain ?: "(empty)",
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.verticalScroll(rememberScrollState()).heightIn(max = 420.dp)
                )
            }
        }
        is LyricsRepository.LyricsState.Failed -> Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            modifier = Modifier.padding(vertical = 12.dp)
        ) {
            Icon(
                Icons.Filled.Error,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.error,
                modifier = Modifier.size(24.dp)
            )
            Text(
                state.message,
                style = MaterialTheme.typography.bodyMedium,
                color = HathorColors.TextBody,
                modifier = Modifier.weight(1f)
            )
            FilledTonalIconButton(
                onClick = onRetry,
                colors = IconButtonDefaults.filledTonalIconButtonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) { Icon(Icons.Filled.Refresh, contentDescription = "Retry loading lyrics") }
        }
    }
}

data class LrcLine(val timeSec: Double, val text: String)

/**
 * Desktop parseLRC(): first [mm:ss.xx] tag per line, empty text -> ♪,
 * untagged lines skipped.
 */
private fun parseLrc(lrc: String): List<LrcLine> {
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

/**
 * Desktop renderLyrics + updateHighlightedLine(): big lines, tap seeks
 * (progress_slider_click equivalent), active line highlighted + followed.
 */
@Composable
private fun SyncedLyrics(lines: List<LrcLine>, player: PlayerManager) {
    val positionMs by player.positionMs.collectAsState()
    if (lines.isEmpty()) {
        Text("No synchronized lyrics found.", style = MaterialTheme.typography.bodyMedium)
        return
    }
    val nowSec = positionMs / 1000.0
    var active = -1
    for (i in lines.indices) {
        if (nowSec >= lines[i].timeSec) active = i else break
    }
    val listState = androidx.compose.foundation.lazy.rememberLazyListState()
    LaunchedEffect(active) {
        if (active >= 0) {
            try {
                listState.animateScrollToItem(active)
            } catch (_: Exception) {
            }
        }
    }
    LazyColumn(
        state = listState,
        modifier = Modifier.heightIn(max = 420.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        itemsIndexed(lines, key = { idx, l -> "$idx-${l.timeSec}" }) { idx, line ->
            Text(
                line.text,
                style = if (idx == active) {
                    MaterialTheme.typography.headlineSmall.copy(color = androidx.compose.ui.graphics.Color.White)
                } else {
                    MaterialTheme.typography.headlineSmall.copy(color = HathorColors.TextHint)
                },
                modifier = Modifier.fillMaxWidth().clickable {
                    player.seekTo((line.timeSec * 1000).toInt())
                }
            )
        }
    }
}

@Composable
private fun LyricsSearch(
    track: String,
    artist: String,
    onTrack: (String) -> Unit,
    onArtist: (String) -> Unit,
    searching: Boolean,
    suggestions: List<LyricSuggestion>,
    onSearch: () -> Unit,
    onPick: (LyricSuggestion) -> Unit
) {
    HathorTextField(value = track, onValueChange = onTrack, label = "Title", modifier = Modifier.fillMaxWidth())
    HathorTextField(value = artist, onValueChange = onArtist, label = "Artist", modifier = Modifier.fillMaxWidth())
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        if (searching) {
            CircularProgressIndicator(
                modifier = Modifier.size(24.dp),
                color = HathorColors.AccentBright,
                strokeWidth = 2.dp
            )
            Text("Searching…", style = MaterialTheme.typography.bodyMedium, color = HathorColors.TextHint)
        } else {
            FilledTonalIconButton(
                onClick = onSearch,
                enabled = track.isNotBlank(),
                colors = IconButtonDefaults.filledTonalIconButtonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) { Icon(Icons.Filled.Search, contentDescription = "Search lyrics") }
            Text(
                if (suggestions.isEmpty()) "No suggestions yet — search, or use Edit."
                else "${suggestions.size} match${if (suggestions.size == 1) "" else "es"} — tap to save.",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
        }
    }
    LazyColumn(modifier = Modifier.heightIn(max = 320.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items(suggestions, key = { it.id }) { s ->
            Row(
                modifier = Modifier.fillMaxWidth().hathorGlass().clickable { onPick(s) }.padding(10.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        s.trackName.ifBlank { "(unknown title)" },
                        style = MaterialTheme.typography.bodyMedium,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                    )
                    Text(
                        listOfNotNull(
                            s.artistName.ifBlank { null },
                            s.albumName,
                            if (s.durationSec > 0) "%d:%02d".format(s.durationSec / 60, s.durationSec % 60) else null,
                            if (s.syncedLyrics != null) "synced" else "plain"
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                Icon(
                    Icons.Filled.Download,
                    contentDescription = "Use these lyrics",
                    tint = HathorColors.AccentBright,
                    modifier = Modifier.size(20.dp)
                )
            }
        }
    }
}

@Composable
private fun LyricsEdit(text: String, onText: (String) -> Unit, saveMsg: String?, onSave: () -> Unit) {
    HathorTextField(
        value = text,
        onValueChange = onText,
        label = "Lyrics (paste or correct)",
        singleLine = false,
        modifier = Modifier.fillMaxWidth().heightIn(min = 200.dp, max = 420.dp)
    )
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        FilledTonalIconButton(
            onClick = onSave,
            colors = IconButtonDefaults.filledTonalIconButtonColors(
                containerColor = HathorColors.Accent,
                contentColor = Color.White
            )
        ) { Icon(Icons.Filled.Check, contentDescription = "Save lyrics") }
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                if (text.isBlank()) "Empty" else "${text.lines().size} line${if (text.lines().size == 1) "" else "s"}",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
            saveMsg?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
        }
    }
}

private fun formatMs(ms: Int): String {
    if (ms <= 0) return "0:00"
    val s = ms / 1000
    return "%d:%02d".format(s / 60, s % 60)
}
