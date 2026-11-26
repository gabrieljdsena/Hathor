@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
package com.musicplayer.android.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
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
import androidx.compose.material.icons.filled.Lyrics
import androidx.compose.material.icons.filled.Mic
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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.FilledTonalIconButton
import androidx.compose.material3.FilterChip
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
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.LyricSuggestion
import com.musicplayer.android.data.LyricsRepository
import com.musicplayer.android.data.LyricsRomanizer
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PodcastChapter
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.ModernSlider
import com.musicplayer.android.ui.theme.hathorGlass
import com.musicplayer.android.ui.theme.hathorPressable
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
    volume: Float,
    onVolumeChange: (Float) -> Unit,
    onVolumeDone: () -> Unit,
    onDismiss: () -> Unit,
    onOpenLyrics: () -> Unit
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

    // Ambient accent follows the cover, crossfading between tracks.
    val coverAccent = rememberArtworkAccent(cover)
    val animatedAccent by animateColorAsState(
        targetValue = coverAccent,
        animationSpec = tween(durationMillis = 450),
        label = "cover-accent"
    )
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = Color(0xFF131316),
        dragHandle = {
            Box(
                modifier = Modifier
                    .padding(top = 10.dp, bottom = 4.dp)
                    .size(width = 40.dp, height = 4.dp)
                    .clip(CircleShape)
                    .background(Color.White.copy(alpha = 0.22f))
            )
        }
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    Brush.verticalGradient(
                        0f to animatedAccent.copy(alpha = 0.30f),
                        0.5f to Color.Transparent
                    )
                )
        ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Box(
                modifier = Modifier
                    .size(240.dp)
                    .shadow(
                        elevation = 24.dp,
                        shape = RoundedCornerShape(24.dp),
                        spotColor = animatedAccent.copy(alpha = 0.55f)
                    )
            ) {
                HathorArtwork(
                    bitmap = cover,
                    contentDescription = "Cover",
                    modifier = Modifier.fillMaxSize(),
                    cornerRadius = 24.dp
                )
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
                    Text(
                        formatMs(position),
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextBody
                    )
                    Text(
                        formatMs(duration),
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint
                    )
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
                    modifier = Modifier
                        .size(72.dp)
                        .shadow(
                            elevation = 18.dp,
                            shape = CircleShape,
                            spotColor = animatedAccent.copy(alpha = 0.65f)
                        )
                        .clip(CircleShape)
                        .background(
                            Brush.linearGradient(
                                listOf(HathorColors.AccentBright, HathorColors.AccentDeep)
                            )
                        )
                        .hathorPressable { player.toggle() },
                    contentAlignment = Alignment.Center
                ) {
                    AnimatedPlayPauseIcon(
                        playing = playing,
                        modifier = Modifier.size(36.dp),
                        tint = Color.White
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
                TextButton(onClick = onOpenLyrics, enabled = current != null) {
                    Icon(Icons.Filled.Lyrics, contentDescription = null, tint = HathorColors.AccentBright)
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
                            HathorArtwork(
                                bitmap = queueCovers[f.absolutePath],
                                contentDescription = null,
                                modifier = Modifier.size(40.dp),
                                cornerRadius = 8.dp
                            )
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

    // The queue-item context menu is above; lyrics move to a full-screen view
    // owned by MainActivity so they are no longer a nested drawer.
}

private fun formatMs(ms: Int): String {
    if (ms <= 0) return "0:00"
    val s = ms / 1000
    return "%d:%02d".format(s / 60, s % 60)
}
