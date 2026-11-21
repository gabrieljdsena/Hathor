@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.musicplayer.android.ui

import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.HistoryItem
import com.musicplayer.android.data.HistoryRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.QueueRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlaybackSource
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import java.io.File

/** Rows composed per page — history tables can grow unbounded, so pages load on scroll. */
private const val HISTORY_PAGE = 50

private fun HistoryItem.toMeta(): SongMeta = SongMeta(
    file = file,
    title = title,
    artist = artist,
    album = "Unknown",
    durationSec = 0L,
    dateDownload = date
)

/**
 * History destination (desktop history view + web History): Download History
 * and Played History tabs, covers, row menus, and a queue built from the full
 * active tab (not just the general library).
 */
@Composable
fun HistoryScreen(
    repo: HistoryRepository,
    queue: QueueRepository,
    metadata: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    musicDir: File,
    nowPlayingFile: String?,
    isPlaying: Boolean,
    onPlaySong: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    onOpenArtist: (String) -> Unit = {}
) {
    var tab by remember { mutableIntStateOf(0) }
    var items by remember { mutableStateOf<List<HistoryItem>>(emptyList()) }
    var refreshing by remember { mutableStateOf(false) }
    var limit by remember { mutableIntStateOf(HISTORY_PAGE) }
    var notice by remember { mutableStateOf<String?>(null) }
    var menuItem by remember { mutableStateOf<HistoryItem?>(null) }
    var editSong by remember { mutableStateOf<SongMeta?>(null) }
    var playlistSong by remember { mutableStateOf<SongMeta?>(null) }
    val scope = rememberCoroutineScope()

    val source = remember(tab) {
        PlaybackSource(if (tab == 0) "download_history" else "recently_played", null)
    }
    val metas = remember(items) { items.map { it.toMeta() } }

    suspend fun reload() {
        items = if (tab == 0) repo.downloaded(musicDir) else repo.played(musicDir)
        limit = HISTORY_PAGE
        refreshing = false
    }
    LaunchedEffect(tab) { reload() }

    // Load-more pagination (same as All Songs / Daily Mix).
    val paged = remember(items, limit) { items.take(limit) }
    val listState = rememberLazyListState()
    LaunchedEffect(listState, paged.size, items.size, limit) {
        snapshotFlow { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index }
            .collectLatest { lastVisible ->
                if (lastVisible != null && lastVisible >= paged.size - 8 && limit < items.size) {
                    limit += HISTORY_PAGE
                }
            }
    }

    PullToRefreshBox(
        isRefreshing = refreshing,
        onRefresh = {
            refreshing = true
            scope.launch { reload() }
        },
        modifier = Modifier.fillMaxSize()
    ) {
        Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("History", style = MaterialTheme.typography.titleLarge)
            Text("Your download and playback history", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                HistoryTab("Download History", selected = tab == 0) { tab = 0 }
                HistoryTab("Played History", selected = tab == 1) { tab = 1 }
            }
            if (items.isEmpty()) {
                Text(if (tab == 0) "Nothing downloaded yet." else "Nothing played yet.")
            }
            notice?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint) }
            LazyColumn(
                state = listState,
                modifier = Modifier.weight(1f).fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(paged, key = { (if (tab == 0) "d" else "p") + it.file + (it.date ?: "") }) { item ->
                    val meta = remember(item) { item.toMeta() }
                    val active = item.file == nowPlayingFile
                    Row(
                        // Fixed three-line card: title / artist / date. Long
                        // metadata can only ellipsize, never change row height.
                        modifier = Modifier.fillMaxWidth().heightIn(min = 76.dp).hathorGlass()
                            .combinedClickable(
                                onClick = { onPlaySong(meta, metas, source) },
                                onLongClick = { menuItem = item }
                            )
                            .animateItemPlacement()
                            .padding(10.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        SongCover(repo = metadata, fileName = item.file, size = 44.dp)
                        Column(
                            modifier = Modifier.weight(1f),
                            verticalArrangement = Arrangement.Center
                        ) {
                            Text(
                                item.title,
                                style = MaterialTheme.typography.bodyMedium,
                                color = if (active) HathorColors.AccentBright else HathorColors.TextPrimary,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                item.artist.ifBlank { "Unknown" },
                                style = MaterialTheme.typography.bodySmall,
                                color = HathorColors.TextHint,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.clickable(
                                    enabled = item.artist.isNotBlank() && item.artist != "Unknown"
                                ) { onOpenArtist(item.artist) }
                            )
                            Text(
                                buildString {
                                    append(item.date ?: "unknown date")
                                    if (active && isPlaying) append(" · Playing")
                                },
                                style = MaterialTheme.typography.bodySmall,
                                color = if (active && isPlaying) HathorColors.AccentBright else HathorColors.TextHint,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                        IconButton(onClick = { menuItem = item }, modifier = Modifier.size(40.dp)) {
                            Icon(Icons.Filled.MoreVert, contentDescription = "Menu", tint = HathorColors.TextHint, modifier = Modifier.size(20.dp))
                        }
                    }
                }
                if (paged.size < items.size) {
                    item(key = "loading-more") {
                        Row(
                            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                            horizontalArrangement = Arrangement.Center,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            androidx.compose.material3.CircularProgressIndicator(
                                modifier = Modifier.padding(end = 8.dp),
                                strokeWidth = 2.dp
                            )
                            Text(
                                "Showing ${paged.size} of ${items.size}…",
                                style = MaterialTheme.typography.bodySmall,
                                color = HathorColors.TextHint
                            )
                        }
                    }
                }
            }
        }
    }

    menuItem?.let { item ->
        val meta = item.toMeta()
        SongMenuDialog(
            song = meta,
            visible = metas,
            source = source,
            onPlay = { s, v, src -> onPlaySong(s, v, src) },
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            onOpenArtist = { onOpenArtist(item.artist) },
            onOpenAlbum = {},
            showNav = item.artist.isNotBlank() && item.artist != "Unknown",
            onDismiss = { menuItem = null },
            onEdit = { editSong = meta },
            onPlaylists = { playlistSong = meta },
            extraActionLabel = if (tab == 0 && !item.link.isNullOrBlank()) "Re-download" else null,
            onExtraAction = if (tab == 0 && !item.link.isNullOrBlank()) {
                {
                    scope.launch {
                        try {
                            queue.submit(item.link, item.title, item.artist.ifBlank { null })
                            notice = "Re-download queued: ${item.title}"
                        } catch (e: Exception) {
                            notice = "Re-download failed: ${e.message}"
                        }
                    }
                }
            } else {
                null
            }
        )
    }

    editSong?.let { song ->
        EditSongDialog(
            song = song,
            repo = metadata,
            playlistRepo = playlistRepo,
            artwork = artwork,
            onPlayNext = { onPlayNext(song); editSong = null },
            onEnqueue = { onEnqueue(song); editSong = null },
            onDismiss = { editSong = null; metadata.clearStatus() },
            playingFile = nowPlayingFile
        )
    }

    playlistSong?.let { song ->
        SongPlaylistsDialog(
            song = song,
            repo = playlistRepo,
            onDismiss = { playlistSong = null }
        )
    }
}

@Composable
private fun HistoryTab(label: String, selected: Boolean, onClick: () -> Unit) {
    Column(modifier = Modifier.clickable(onClick = onClick).padding(bottom = 4.dp)) {
        Text(
            label,
            color = if (selected) HathorColors.AccentBright else HathorColors.TextHint,
            style = if (selected) MaterialTheme.typography.titleSmall else MaterialTheme.typography.bodyMedium
        )
    }
}
