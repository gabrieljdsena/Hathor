@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.musicplayer.android.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.HistoryItem
import com.musicplayer.android.data.HistoryRepository
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import java.io.File

/** Rows composed per page — history tables can grow unbounded, so pages load on scroll. */
private const val HISTORY_PAGE = 50

/**
 * History destination (desktop history view: Download History + Played
 * History tabs). Tap a row to play it.
 */
@Composable
fun HistoryScreen(
    repo: HistoryRepository,
    queue: com.musicplayer.android.data.QueueRepository,
    musicDir: File,
    onPlayFile: (File) -> Unit,
    onOpenArtist: (String) -> Unit = {}
) {
    var tab by remember { mutableIntStateOf(0) }
    var items by remember { mutableStateOf<List<HistoryItem>>(emptyList()) }
    var refreshing by remember { mutableStateOf(false) }
    var limit by remember { mutableIntStateOf(HISTORY_PAGE) }
    var notice by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun reload() {
        items = if (tab == 0) repo.downloaded(musicDir) else repo.played(musicDir)
        limit = HISTORY_PAGE
        refreshing = false
    }
    LaunchedEffect(tab) { reload() }

    // Load-more pagination (same as All Songs / Daily Mix): only the first
    // pages are composed; scrolling near the end appends the next page.
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
                Row(
                    modifier = Modifier.fillMaxWidth().hathorGlass().animateItemPlacement().clickable { onPlayFile(File(musicDir, item.file)) }.padding(10.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(item.title, style = MaterialTheme.typography.bodyMedium)
                        // Desktop artist link opens the artist page.
                        Text(
                            "${item.artist} · ${item.date ?: "unknown date"}",
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.clickable(
                                enabled = item.artist.isNotBlank() && item.artist != "Unknown"
                            ) { onOpenArtist(item.artist) }
                        )
                    }
                    // Desktop re-download button for download rows with a link.
                    if (tab == 0 && !item.link.isNullOrBlank()) {
                        TextButton(
                            onClick = {
                                scope.launch {
                                    try {
                                        queue.submit(item.link, item.title, item.artist.ifBlank { null })
                                        notice = "Re-download queued: ${item.title}"
                                    } catch (e: Exception) {
                                        notice = "Re-download failed: ${e.message}"
                                    }
                                }
                            }
                        ) { Text("Re-download") }
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
