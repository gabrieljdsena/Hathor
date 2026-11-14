package com.musicplayer.android.ui

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Explore
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
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
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.QueueRepository
import com.musicplayer.android.data.api.DiscoverApi
import com.musicplayer.android.data.api.DiscoverItem
import com.musicplayer.android.engine.DownloadEngine
import com.musicplayer.android.playback.PreviewPlayer
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.hathorGlass
import com.musicplayer.android.util.FuzzySearch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val SOURCES = listOf(
    "all" to "All",
    "similar" to "Similar tracks",
    "cf" to "Similar listeners",
    "graph" to "Related artists",
    "tag" to "From your genres",
    "audio" to "Sounds like yours",
    "popular" to "Popular",
    "artist" to "From your artists",
    "chart" to "Trending",
    "llm" to "Picked for you"
)

/**
 * Discover feed from the Hathor Web API. Recommendations come from the
 * server; only the download itself runs on-device via the YouTube engine.
 */
@Composable
fun DiscoverScreen(
    metadata: MetadataRepository,
    queue: QueueRepository,
    engine: DownloadEngine,
    preview: PreviewPlayer,
    onPreviewStarted: () -> Unit = {}
) {
    var items by remember { mutableStateOf<List<DiscoverItem>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    var refreshing by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var query by remember { mutableStateOf("") }
    var sourceFilter by remember { mutableStateOf("all") }
    var busyKeys by remember { mutableStateOf<Set<String>>(emptySet()) }
    var previewKey by remember { mutableStateOf<String?>(null) }
    var confirmItem by remember { mutableStateOf<DiscoverItem?>(null) }
    val scope = rememberCoroutineScope()

    fun key(item: DiscoverItem) = "${item.title}|${item.artist}"

    suspend fun load(refresh: Boolean) {
        try {
            error = null
            val feed = if (refresh) DiscoverApi.refresh() else DiscoverApi.load()
            items = feed.items
        } catch (e: Exception) {
            error = e.message ?: "Could not load Discover."
        } finally {
            loading = false
            refreshing = false
        }
    }

    LaunchedEffect(Unit) { load(refresh = false) }

    fun download(item: DiscoverItem) {
        scope.launch {
            busyKeys = busyKeys + key(item)
            try {
                queue.submit("", item.title, item.artist)
                runCatching { DiscoverApi.feedback(item, "download") }
                error = null
            } catch (e: Exception) {
                error = "Download failed: ${e.message}"
            } finally {
                busyKeys = busyKeys - key(item)
            }
        }
    }

    fun preview(item: DiscoverItem) {
        val k = key(item)
        if (previewKey == k) {
            preview.stop()
            previewKey = null
            return
        }
        scope.launch {
            previewKey = k
            val result = withContext(Dispatchers.IO) {
                engine.searchYouTube("${item.title} ${item.artist}", 1)
            }
            val hit = result.getOrNull()?.firstOrNull()
            if (hit == null) {
                error = "No YouTube preview found for ${item.title}."
                previewKey = null
            } else {
                runCatching { DiscoverApi.feedback(item, "preview") }
                preview.toggle(hit.id, hit.url, engine, onPreviewStarted)
            }
        }
    }

    val visible = remember(items, query, sourceFilter) {
        items.filter { item ->
            (sourceFilter == "all" || item.source == sourceFilter) &&
                (query.isBlank() || FuzzySearch.matchesFields(listOf(item.title, item.artist, item.album), query))
        }
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            HeaderIconTile {
                Icon(Icons.Filled.Explore, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text("Discover", style = MaterialTheme.typography.titleLarge)
                Text(
                    if (items.isEmpty()) "Recommendations from your Web API"
                    else "${items.size} recommendations",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            }
            IconButton(
                onClick = {
                    refreshing = true
                    scope.launch { load(refresh = true) }
                },
                enabled = !refreshing && !loading
            ) {
                Icon(Icons.Filled.Refresh, contentDescription = "Refresh Discover", tint = HathorColors.TextHint)
            }
        }
        HathorTextField(
            value = query,
            onValueChange = { query = it },
            label = "Search recommendations…",
            modifier = Modifier.fillMaxWidth()
        )
        Row(
            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            SOURCES.forEach { (id, label) ->
                FilterChip(
                    selected = sourceFilter == id,
                    onClick = { sourceFilter = id },
                    label = { Text(label) }
                )
            }
        }
        error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
        when {
            loading -> Text("Loading recommendations…", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            visible.isEmpty() -> Text(
                if (items.isEmpty()) "No recommendations yet — try Refresh." else "No cards match this filter.",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
            else -> LazyVerticalGrid(
                columns = GridCells.Fixed(2),
                modifier = Modifier.weight(1f).fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                items(visible, key = { key(it) }) { item ->
                    DiscoverCard(
                        item = item,
                        busy = key(item) in busyKeys,
                        previewing = previewKey == key(item),
                        onDownload = {
                            val owned = metadata.library.value.firstOrNull { song ->
                                FuzzySearch.matchesFields(listOf(song.title, song.artist), item.title)
                            }
                            if (owned != null) confirmItem = item else download(item)
                        },
                        onPreview = { preview(item) },
                        onDismiss = {
                            items = items.filterNot { key(it) == key(item) }
                            scope.launch { runCatching { DiscoverApi.feedback(item, "dismiss") } }
                        }
                    )
                }
            }
        }
    }

    confirmItem?.let { item ->
        AlertDialog(
            onDismissRequest = { confirmItem = null },
            title = { Text("Already in your library?") },
            text = { Text("\"${item.title}\" by ${item.artist} looks like a song you already have. Download again?") },
            confirmButton = {
                Button(
                    onClick = {
                        val target = item
                        confirmItem = null
                        download(target)
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White
                    )
                ) { Text("Download anyway") }
            },
            dismissButton = {
                TextButton(onClick = { confirmItem = null }) { Text("Cancel") }
            }
        )
    }
}

@Composable
private fun DiscoverCard(
    item: DiscoverItem,
    busy: Boolean,
    previewing: Boolean,
    onDownload: () -> Unit,
    onPreview: () -> Unit,
    onDismiss: () -> Unit
) {
    Column(modifier = Modifier.fillMaxWidth().hathorGlass().padding(10.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        UrlThumb(url = item.artworkUrl.ifBlank { null }, size = 140.dp)
        Text(
            SOURCES.firstOrNull { it.first == item.source }?.second ?: item.source,
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.AccentBright
        )
        Text(item.title, style = MaterialTheme.typography.bodyMedium, maxLines = 2)
        Text(item.artist, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint, maxLines = 1)
        Text(
            listOfNotNull(
                item.album.takeIf { it.isNotBlank() },
                item.year.takeIf { it.isNotBlank() }
            ).joinToString(" · ").ifBlank { " " },
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint,
            maxLines = 1
        )
        item.reason?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint, maxLines = 2)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(2.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onDownload, enabled = !busy) {
                Icon(
                    Icons.Filled.Download,
                    contentDescription = "Download ${item.title}",
                    tint = if (busy) HathorColors.TextHint else HathorColors.AccentBright,
                    modifier = Modifier.size(20.dp)
                )
            }
            IconButton(onClick = onPreview) {
                Icon(
                    Icons.Filled.PlayArrow,
                    contentDescription = "Preview ${item.title}",
                    tint = if (previewing) HathorColors.AccentBright else HathorColors.TextHint,
                    modifier = Modifier.size(20.dp)
                )
            }
            IconButton(onClick = onDismiss) {
                Icon(
                    Icons.Filled.Close,
                    contentDescription = "Dismiss ${item.title}",
                    tint = HathorColors.TextHint,
                    modifier = Modifier.size(20.dp)
                )
            }
        }
    }
}
