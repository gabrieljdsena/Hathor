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
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
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
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlaybackSource
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HeaderIconTile
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest

private const val ALL_PAGE = 50

/**
 * All Songs view (desktop all_songs.html — the old home song table moved
 * here unchanged in behavior): search, sortable columns
 * (Title/Album/Duration/Downloaded), lazy-loaded covers, play-all button,
 * back button. Artist/album detail preserves the artist/album queue contexts.
 */
@Composable
fun AllSongsScreen(
    repo: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    initialArtist: String? = null,
    onInitialConsumed: () -> Unit = {},
    onBack: () -> Unit,
    onPlayAll: (List<SongMeta>, PlaybackSource) -> Unit,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    val songs by repo.library.collectAsState()
    var query by remember { mutableStateOf("") }
    var sortKey by remember { mutableStateOf<String?>(null) }
    var sortAsc by remember { mutableStateOf(true) }
    var openArtist by remember { mutableStateOf<String?>(null) }
    var openAlbum by remember { mutableStateOf<String?>(null) }
    var menuSong by remember { mutableStateOf<SongMeta?>(null) }

    LaunchedEffect(Unit) { repo.refreshLibrary() }
    // Deep-link entry (e.g. history artist link): open once, then consume.
    LaunchedEffect(initialArtist) {
        if (!initialArtist.isNullOrBlank()) {
            openArtist = initialArtist
            onInitialConsumed()
        }
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
    // Pagination: only the first page is composed. The limit grows
    // automatically as the user scrolls (lazy loading), so CPU/RAM stay
    // flat no matter how big the library is. Resets on search/sort/refresh.
    var limit by remember { mutableIntStateOf(ALL_PAGE) }
    LaunchedEffect(sorted) { limit = ALL_PAGE }
    val paged = remember(sorted, limit) { sorted.take(limit) }
    val listState = rememberLazyListState()
    // Keys include paged.size/limit so the collector always sees fresh
    // values (otherwise the closure would go stale after the first page).
    LaunchedEffect(listState, paged.size, sorted.size, limit) {
        snapshotFlow { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index }
            .collectLatest { lastVisible ->
                if (lastVisible != null && lastVisible >= paged.size - 8 && limit < sorted.size) {
                    limit += ALL_PAGE
                }
            }
    }

    openArtist?.let { name ->
        val artistSongs = visible.filter { it.artist == name }
            .sortedWith(compareBy({ it.album.lowercase() }, { it.title.lowercase() }))
        ArtistSongsView(
            name = name,
            songs = artistSongs,
            repo = repo,
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying,
            onBack = { openArtist = null },
            onPlay = { song ->
                onPlay(song, artistSongs, PlaybackSource("artist", name))
            }
        )
        return
    }
    openAlbum?.let { name ->
        val albumSongs = visible.filter { it.album == name }
            .sortedWith(compareBy({ it.artist.lowercase() }, { it.title.lowercase() }))
        AlbumSongsView(
            name = name,
            subtitle = albumSongs.firstOrNull()?.artist?.takeIf { it != "Unknown" } ?: "Unknown",
            songs = albumSongs,
            repo = repo,
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying,
            onBack = { openAlbum = null },
            onPlay = { song ->
                onPlay(song, albumSongs, PlaybackSource("album", name))
            }
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        // Header: back-to-Home + title/count + play-all.
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Home", tint = HathorColors.TextBody)
            }
            HeaderIconTile {
                Icon(Icons.Filled.MusicNote, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    "All Songs",
                    style = MaterialTheme.typography.titleLarge,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
                Text(
                    "${sorted.size} song${if (sorted.size == 1) "" else "s"}",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
            }
            androidx.compose.material3.FilledIconButton(
                onClick = { onPlayAll(sorted, PlaybackSource("all_songs", null)) },
                enabled = sorted.isNotEmpty()
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = "Play all")
            }
        }
        // Search
        HathorTextField(
            value = query,
            onValueChange = { query = it },
            label = "Search…",
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
        // Sortable columns (Title/Album/Duration/Downloaded).
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
        // Plain paginated song list: no grouping header above it. The old
        // Artists/Albums grouping Column ate all vertical space, leaving the
        // LazyColumn with ~0 height so it looked unscrollable. Navigation to
        // artist/album stays via each row's menu ("Go to artist/album").
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(paged, key = { it.file }) { song ->
                SongRow(
                    song = song,
                    repo = repo,
                    isActive = song.file == nowPlayingFile,
                    isPlaying = isPlaying && song.file == nowPlayingFile,
                    onClick = {
                        onPlay(song, sorted, PlaybackSource("all_songs", null))
                    },
                    onMenu = { menuSong = song }
                )
            }
            if (paged.size < sorted.size) {
                // Lazy-loading footer: more rows compose as you scroll.
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
            source = PlaybackSource("all_songs", null),
            onPlay = { s, v, src -> onPlay(s, v, src) },
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            onOpenArtist = { openArtist = song.artist.takeIf { it.isNotBlank() && it != "Unknown" } },
            onOpenAlbum = { openAlbum = song.album.takeIf { it.isNotBlank() && it != "Unknown" } },
            onDismiss = { menuSong = null }
        )
    }
}

/** Context menu Play action: builds the queue from the visible list + context. */
@Composable
fun SongMenuDialog(
    song: SongMeta,
    visible: List<SongMeta>,
    source: PlaybackSource,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    onOpenArtist: () -> Unit,
    onOpenAlbum: () -> Unit,
    onDismiss: () -> Unit,
    showNav: Boolean = true
) {
    androidx.compose.material3.AlertDialog(
        onDismissRequest = onDismiss,
        title = { DialogTitleBar(title = song.title.ifBlank { song.file }, onClose = onDismiss) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    listOfNotNull(
                        song.artist.takeIf { it.isNotBlank() },
                        song.album.takeIf { it.isNotBlank() }
                    ).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            }
        },
        confirmButton = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Button(
                    onClick = { onPlay(song, visible, source); onDismiss() },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent, contentColor = Color.White
                    ),
                    modifier = Modifier.fillMaxWidth()
                ) { Text("Play") }
                androidx.compose.material3.OutlinedButton(
                    onClick = { onPlayNext(song); onDismiss() },
                    modifier = Modifier.fillMaxWidth()
                ) { Text("Play next") }
                androidx.compose.material3.OutlinedButton(
                    onClick = { onEnqueue(song); onDismiss() },
                    modifier = Modifier.fillMaxWidth()
                ) { Text("Add to queue") }
                if (showNav && song.artist.isNotBlank() && song.artist != "Unknown") {
                    androidx.compose.material3.TextButton(onClick = { onOpenArtist(); onDismiss() }) {
                        Text("Go to artist")
                    }
                }
                if (showNav && song.album.isNotBlank() && song.album != "Unknown") {
                    androidx.compose.material3.TextButton(onClick = { onOpenAlbum(); onDismiss() }) {
                        Text("Go to album")
                    }
                }
            }
        }
    )
}

@Composable
private fun ArtistSongsView(
    name: String,
    songs: List<SongMeta>,
    repo: MetadataRepository,
    nowPlayingFile: String?,
    isPlaying: Boolean,
    onBack: () -> Unit,
    onPlay: (SongMeta) -> Unit
) {
    var limit by remember(songs) { mutableIntStateOf(ALL_PAGE) }
    val paged = remember(songs, limit) { songs.take(limit) }
    val listState = rememberLazyListState()
    LaunchedEffect(listState, paged.size, songs.size, limit) {
        snapshotFlow { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index }
            .collectLatest { lastVisible ->
                if (lastVisible != null && lastVisible >= paged.size - 8 && limit < songs.size) {
                    limit += ALL_PAGE
                }
            }
    }
    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Back", tint = HathorColors.TextBody)
            }
            Column {
                Text(name, style = MaterialTheme.typography.titleMedium)
                Text("${songs.size} songs", style = MaterialTheme.typography.bodySmall)
            }
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(paged, key = { it.file }) { song ->
                SongRow(
                    song = song,
                    repo = repo,
                    isActive = song.file == nowPlayingFile,
                    isPlaying = isPlaying && song.file == nowPlayingFile,
                    onClick = { onPlay(song) }
                )
            }
            if (paged.size < songs.size) {
                item(key = "loading-more") {
                    PagedFooter(shown = paged.size, total = songs.size)
                }
            }
        }
    }
}

@Composable
private fun AlbumSongsView(
    name: String,
    subtitle: String,
    songs: List<SongMeta>,
    repo: MetadataRepository,
    nowPlayingFile: String?,
    isPlaying: Boolean,
    onBack: () -> Unit,
    onPlay: (SongMeta) -> Unit
) {
    var limit by remember(songs) { mutableIntStateOf(ALL_PAGE) }
    val paged = remember(songs, limit) { songs.take(limit) }
    val listState = rememberLazyListState()
    LaunchedEffect(listState, paged.size, songs.size, limit) {
        snapshotFlow { listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index }
            .collectLatest { lastVisible ->
                if (lastVisible != null && lastVisible >= paged.size - 8 && limit < songs.size) {
                    limit += ALL_PAGE
                }
            }
    }
    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Back", tint = HathorColors.TextBody)
            }
            Column {
                Text(name, style = MaterialTheme.typography.titleMedium)
                Text("$subtitle · ${songs.size} songs", style = MaterialTheme.typography.bodySmall)
            }
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(paged, key = { it.file }) { song ->
                SongRow(
                    song = song,
                    repo = repo,
                    isActive = song.file == nowPlayingFile,
                    isPlaying = isPlaying && song.file == nowPlayingFile,
                    onClick = { onPlay(song) }
                )
            }
            if (paged.size < songs.size) {
                item(key = "loading-more") {
                    PagedFooter(shown = paged.size, total = songs.size)
                }
            }
        }
    }
}

/** Shared lazy-loading footer: shown while more pages load on scroll. */
@Composable
private fun PagedFooter(shown: Int, total: Int) {
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
            "Showing $shown of $total…",
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint
        )
    }
}
