package com.musicplayer.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.DailyMixRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlaybackSource
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HeaderIconTile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Locale

private const val MIX_PAGE = 50

/**
 * Daily Mix view (desktop daily_mix.html): Play + Shuffle-play buttons,
 * search, sort, back-to-Home button. The mix is generated once per calendar
 * day and the stored order is stable across restarts.
 */
@Composable
fun DailyMixScreen(
    dailyMix: DailyMixRepository,
    library: MetadataRepository,
    onBack: () -> Unit,
    onPlay: (SongMeta, List<SongMeta>, String?) -> Unit,
    onPlayAll: (List<SongMeta>, String?, Boolean) -> Unit,
    onPlayNext: (SongMeta) -> Unit = {},
    onEnqueue: (SongMeta) -> Unit = {},
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    var songs by remember { mutableStateOf<List<SongMeta>>(emptyList()) }
    var date by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    var query by remember { mutableStateOf("") }
    var sortKey by remember { mutableStateOf<String?>(null) }
    var sortAsc by remember { mutableStateOf(true) }
    var menuSong by remember { mutableStateOf<SongMeta?>(null) }

    LaunchedEffect(Unit) {
        try {
            val mix = withContext(Dispatchers.IO) { dailyMix.getDailyMix() }
            songs = mix.songs
            date = mix.date
        } catch (_: Exception) {
        }
        loading = false
    }

    var debouncedQuery by remember { mutableStateOf("") }
    LaunchedEffect(query) {
        delay(300)
        debouncedQuery = query
    }

    val visible = remember(songs, debouncedQuery) {
        val q = debouncedQuery.trim().lowercase()
        if (q.isBlank()) songs else songs.filter {
            it.title.lowercase().contains(q) || it.artist.lowercase().contains(q) || it.album.lowercase().contains(q)
        }
    }
    val sorted = remember(visible, sortKey, sortAsc) {
        val base = when (sortKey) {
            "Title" -> visible.sortedBy { it.title.lowercase() }
            "Album" -> visible.sortedBy { it.album.lowercase() }
            "Duration" -> visible.sortedBy { it.durationSec }
            "Downloaded" -> visible.sortedBy { it.dateDownload ?: "" }
            else -> visible
        }
        if (sortKey != null && !sortAsc) base.reversed() else base
    }
    // Same load-more pagination as All Songs: 50-row pages composed on
    // demand, auto-appended on scroll so CPU/RAM stay flat.
    var limit by remember { mutableIntStateOf(MIX_PAGE) }
    LaunchedEffect(sorted) { limit = MIX_PAGE }
    val paged = remember(sorted, limit) { sorted.take(limit) }
    val listState = rememberLazyListState()
    LaunchedEffect(listState, paged.size, sorted.size, limit) {
        snapshotFlow { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index }
            .collectLatest { lastVisible ->
                if (lastVisible != null && lastVisible >= paged.size - 8 && limit < sorted.size) {
                    limit += MIX_PAGE
                }
            }
    }

    val dateLabel = remember(date) {
        val d = date ?: return@remember ""
        try {
            val parsed = SimpleDateFormat("yyyy-MM-dd", Locale.US).parse(d) ?: return@remember d
            SimpleDateFormat("EEE, MMM d", Locale.getDefault()).format(parsed)
        } catch (_: Exception) {
            d
        }
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        // Header: back-to-Home + title/date on its own row. The Play/Shuffle
        // actions live on a second row: cramming back + tile + title + two
        // buttons into one Row left the title ~0dp wide on narrow phones, so
        // it wrapped into a giant column and the song list got no height.
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Home", tint = HathorColors.TextBody)
            }
            HeaderIconTile {
                Icon(Icons.Filled.Refresh, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    "Daily Mix",
                    style = MaterialTheme.typography.titleLarge,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
                Text(
                    if (loading) "Generating…" else "${sorted.size} songs${if (dateLabel.isNotBlank()) " · $dateLabel" else ""}",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
            }
        }
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = { onPlayAll(sorted, date, false) },
                enabled = sorted.isNotEmpty(),
                colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White),
                modifier = Modifier.weight(1f)
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = null)
                Text(" Play all")
            }
            OutlinedButton(
                onClick = { onPlayAll(sorted, date, true) },
                enabled = sorted.isNotEmpty(),
                modifier = Modifier.weight(1f)
            ) {
                Icon(Icons.Filled.Refresh, contentDescription = "Shuffle play")
                Text(" Shuffle")
            }
        }
        HathorTextField(
            value = query,
            onValueChange = { query = it },
            label = "Search mix…",
            leadingIcon = { Icon(Icons.Filled.Search, contentDescription = "Search") },
            trailingIcon = {
                if (query.isNotEmpty()) {
                    IconButton(onClick = { query = "" }) {
                        Icon(Icons.Filled.Clear, contentDescription = "Clear")
                    }
                }
            },
            modifier = Modifier.fillMaxWidth()
        )
        SortHeader(
            keys = listOf("Title", "Album", "Duration", "Downloaded"),
            sortKey = sortKey,
            ascending = sortAsc,
            onPick = { key ->
                if (sortKey == key) sortAsc = !sortAsc
                else {
                    sortKey = key
                    sortAsc = true
                }
            }
        )
        if (!loading && sorted.isEmpty()) {
            Text(
                "Nothing mixed yet — play some songs and download music.",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(paged, key = { it.file }) { song ->
                SongRow(
                    song = song,
                    repo = library,
                    isActive = song.file == nowPlayingFile,
                    isPlaying = isPlaying && song.file == nowPlayingFile,
                    onClick = { onPlay(song, sorted, date) },
                    onMenu = { menuSong = song }
                )
            }
            if (paged.size < sorted.size) {
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
                            "Showing ${paged.size} of ${sorted.size}…",
                            style = MaterialTheme.typography.bodySmall,
                            color = HathorColors.TextHint
                        )
                    }
                }
            }
        }
    }

    menuSong?.let { song ->
        SongMenuDialog(
            song = song,
            visible = sorted,
            source = PlaybackSource("daily_mix", date),
            onPlay = { s, v, src -> onPlay(s, v, src.id) },
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            onOpenArtist = {},
            onOpenAlbum = {},
            showNav = false,
            onDismiss = { menuSong = null }
        )
    }
}
