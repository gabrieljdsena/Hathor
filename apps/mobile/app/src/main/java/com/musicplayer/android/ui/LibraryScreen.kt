@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.musicplayer.android.ui

import android.graphics.BitmapFactory
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PlaylistAdd
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.hathorGlass
import com.musicplayer.android.ui.theme.hathorPressable
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Rows rendered per page; lists grow via "Show more", never all at once. */
private const val LIB_PAGE = 50

/**
 * Library tab: the on-device song list (desktop send_song_list view) with
 * cover thumbnails, plus the edit-metadata dialog (desktop edit flow:
 * Title/Artist/Album/Year, remove cover, save, delete).
 */
@Composable
fun LibraryScreen(
    repo: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    onPlay: (SongMeta) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String? = null,
    isPlaying: Boolean = false
) {
    val songs by repo.library.collectAsState()
    val status by repo.status.collectAsState()
    var editing by remember { mutableStateOf<SongMeta?>(null) }
    var mode by remember { mutableIntStateOf(0) } // 0 songs, 1 artists, 2 albums
    var query by remember { mutableStateOf("") }
    var refreshing by remember { mutableStateOf(false) }
    // Desktop home column sorts (_homeSortColumn). Null = folder order.
    var sortKey by remember { mutableStateOf<String?>(null) }
    var sortAsc by remember { mutableStateOf(true) }
    var openArtist by remember { mutableStateOf<String?>(null) }
    var openAlbum by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) { repo.refreshLibrary() }

    // Debounced search: without this every keystroke re-filters + re-sorts
    // the whole library (O(N log N) per frame while typing).
    var debouncedQuery by remember { mutableStateOf("") }
    LaunchedEffect(query) {
        delay(300)
        debouncedQuery = query
    }

    // Desktop home search: one field filters songs (title/artist/album);
    // artist/album modes filter by name with the same field.
    val visibleSongs = remember(songs, debouncedQuery) {
        val q = debouncedQuery.trim().lowercase()
        if (q.isBlank()) songs else songs.filter {
            it.title.lowercase().contains(q) || it.artist.lowercase().contains(q) || it.album.lowercase().contains(q)
        }
    }
    val sortedSongs = remember(visibleSongs, sortKey, sortAsc) {
        val base = when (sortKey) {
            "Title" -> visibleSongs.sortedBy { it.title.lowercase() }
            "Artist" -> visibleSongs.sortedBy { it.artist.lowercase() }
            "Album" -> visibleSongs.sortedBy { it.album.lowercase() }
            "Duration" -> visibleSongs.sortedBy { it.durationSec }
            "Downloaded" -> visibleSongs.sortedBy { it.dateDownload ?: "" }
            else -> visibleSongs
        }
        if (sortKey != null && !sortAsc) base.reversed() else base
    }
    // Group counts feed the Artists/Albums tabs; remembered so typing or
    // scrolling never recomputes them.
    val artistCounts = remember(visibleSongs) {
        visibleSongs.map { it.artist }
            .filter { it.isNotBlank() && it != "Unknown" }
            .groupingBy { it }.eachCount().toList().sortedBy { it.first.lowercase() }
    }
    val albumCounts = remember(visibleSongs) {
        visibleSongs.map { it.album }
            .filter { it.isNotBlank() && it != "Unknown" }
            .groupingBy { it }.eachCount().toList().sortedBy { it.first.lowercase() }
    }
    // Pagination: only the first page is composed; "Show more" appends.
    // Limits reset whenever the source list changes (search/sort/refresh).
    var songLimit by remember { mutableIntStateOf(LIB_PAGE) }
    var artistLimit by remember { mutableIntStateOf(LIB_PAGE) }
    var albumLimit by remember { mutableIntStateOf(LIB_PAGE) }
    LaunchedEffect(sortedSongs) { songLimit = LIB_PAGE }
    LaunchedEffect(artistCounts) { artistLimit = LIB_PAGE }
    LaunchedEffect(albumCounts) { albumLimit = LIB_PAGE }

    openArtist?.let { name ->
        ArtistDetail(
            name = name,
            songs = visibleSongs.filter { it.artist == name }
                .sortedWith(compareBy({ it.album.lowercase() }, { it.title.lowercase() })),
            artwork = artwork,
            onBack = { openArtist = null },
            onPlay = onPlay,
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying
        )
        return
    }
    openAlbum?.let { name ->
        AlbumDetail(
            name = name,
            songs = visibleSongs.filter { it.album == name }
                .sortedWith(compareBy({ it.artist.lowercase() }, { it.title.lowercase() })),
            artwork = artwork,
            onBack = { openAlbum = null },
            onPlay = onPlay,
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying
        )
        return
    }

    PullToRefreshBox(
        isRefreshing = refreshing,
        onRefresh = {
            scope.launch {
                refreshing = true
                repo.refreshLibrary()
                refreshing = false
            }
        },
        modifier = Modifier.fillMaxSize()
    ) {
    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Text("Library (${songs.size})", style = MaterialTheme.typography.titleLarge)
            IconButton(
                onClick = {
                    scope.launch {
                        refreshing = true
                        repo.refreshLibrary()
                        refreshing = false
                    }
                }
            ) {
                Icon(Icons.Filled.Refresh, contentDescription = "Refresh", tint = com.musicplayer.android.ui.theme.HathorColors.TextHint)
            }
        }
        // Desktop home groups: songs, artists, albums.
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            LibraryModeTab("Songs", selected = mode == 0) { mode = 0 }
            LibraryModeTab("Artists", selected = mode == 1) { mode = 1 }
            LibraryModeTab("Albums", selected = mode == 2) { mode = 2 }
        }
        // Desktop home search bar (glass pill, orange focus).
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
        status?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
        when (mode) {
            0 -> {
                SortHeader(
                    keys = listOf("Title", "Artist", "Album", "Duration", "Downloaded"),
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
                SongsList(
                    songs = sortedSongs.take(songLimit),
                    shown = minOf(songLimit, sortedSongs.size),
                    total = sortedSongs.size,
                    onLoadMore = { songLimit += LIB_PAGE },
                    repo = repo,
                    onPlay = onPlay,
                    onEdit = { editing = it; repo.clearStatus() },
                    nowPlayingFile = nowPlayingFile,
                    isPlaying = isPlaying
                )
            }
            1 -> ArtistsGrid(
                artists = artistCounts.take(artistLimit),
                shown = minOf(artistLimit, artistCounts.size),
                total = artistCounts.size,
                onLoadMore = { artistLimit += LIB_PAGE },
                artwork = artwork,
                onOpen = { openArtist = it }
            )
            else -> AlbumsGrid(
                albums = albumCounts.take(albumLimit),
                shown = minOf(albumLimit, albumCounts.size),
                total = albumCounts.size,
                onLoadMore = { albumLimit += LIB_PAGE },
                songs = visibleSongs,
                artwork = artwork,
                onOpen = { openAlbum = it }
            )
        }
    }
    }

    editing?.let { song ->
        EditSongDialog(
            song = song,
            repo = repo,
            playlistRepo = playlistRepo,
            artwork = artwork,
            onPlayNext = { onPlayNext(song); editing = null },
            onEnqueue = { onEnqueue(song); editing = null },
            onDismiss = { editing = null; repo.clearStatus() }
        )
    }
}

/**
 * Desktop column sorts (_homeSortColumn / _pvSortColumn): tap a key to sort
 * ascending, tap again to flip. Shared by the song list and playlist detail.
 */
@Composable
fun SortHeader(
    keys: List<String>,
    sortKey: String?,
    ascending: Boolean,
    onPick: (String) -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        keys.forEach { key ->
            val active = sortKey == key
            TextButton(onClick = { onPick(key) }) {
                Text(
                    key,
                    color = if (active) com.musicplayer.android.ui.theme.HathorColors.AccentBright
                        else com.musicplayer.android.ui.theme.HathorColors.TextHint,
                    style = MaterialTheme.typography.bodySmall
                )
                if (active) {
                    Icon(
                        if (ascending) Icons.Filled.ArrowUpward else Icons.Filled.ArrowDownward,
                        contentDescription = if (ascending) "Ascending" else "Descending",
                        tint = com.musicplayer.android.ui.theme.HathorColors.AccentBright,
                        modifier = Modifier.size(14.dp)
                    )
                }
            }
        }
    }
}

@Composable
private fun LibraryModeTab(label: String, selected: Boolean, onClick: () -> Unit) {    Column(modifier = Modifier.clickable(onClick = onClick).padding(bottom = 4.dp)) {
        Text(
            label,
            color = if (selected) com.musicplayer.android.ui.theme.HathorColors.AccentBright
                else com.musicplayer.android.ui.theme.HathorColors.TextHint,
            style = if (selected) MaterialTheme.typography.titleSmall else MaterialTheme.typography.bodyMedium
        )
    }
}

@Composable
private fun ColumnScope.SongsList(
    songs: List<SongMeta>,
    shown: Int,
    total: Int,
    onLoadMore: () -> Unit,
    repo: MetadataRepository,
    onPlay: (SongMeta) -> Unit,
    onEdit: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    // weight(1f) gives the list the remaining height so it scrolls instead
    // of overflowing; fillMaxSize here would break scrolling inside Column.
    LazyColumn(modifier = Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items(songs, key = { it.file }) { song ->
            Row(
                modifier = Modifier.fillMaxWidth()
                    .hathorGlass()
                    .hathorPressable { onPlay(song) }
                    .animateItemPlacement()
                    .padding(8.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                // Desktop .list-visualizer: animated bars on the playing row.
                if (song.file == nowPlayingFile && isPlaying) {
                    VisualizerBars(modifier = Modifier.size(56.dp, 20.dp))
                } else {
                    CoverThumb(repo = repo, fileName = song.file)
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(song.title, style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "${song.artist} · ${song.album} · ${formatDuration(song.durationSec)}",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                    TextButton(onClick = { onEdit(song) }) {
                        Icon(Icons.Filled.Edit, contentDescription = "Edit", tint = com.musicplayer.android.ui.theme.HathorColors.TextHint)
                    }
            }
        }
        if (shown < total) {
            item(key = "show-more") {
                MoreRow(shown = shown, total = total, onLoadMore = onLoadMore)
            }
        }
    }
}

@Composable
private fun ColumnScope.ArtistsGrid(
    artists: List<Pair<String, Int>>,
    shown: Int,
    total: Int,
    onLoadMore: () -> Unit,
    artwork: ArtworkRepository,
    onOpen: (String) -> Unit
) {
    if (total == 0) {
        Text("No artists yet — tags without an artist show under Songs.")
        return
    }
    LazyColumn(modifier = Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items(artists, key = { it.first }) { (name, count) ->
            var url by remember(name) { mutableStateOf<String?>(null) }
            LaunchedEffect(name) { url = artwork.artistImage(name) }
            Row(
                modifier = Modifier.fillMaxWidth().clickable { onOpen(name) },
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                UrlThumb(url = url, size = 56.dp)
                Column(modifier = Modifier.weight(1f)) {
                    Text(name, style = MaterialTheme.typography.bodyMedium)
                    Text("$count songs", style = MaterialTheme.typography.bodySmall)
                }
            }
        }
        if (shown < total) {
            item(key = "show-more") {
                MoreRow(shown = shown, total = total, onLoadMore = onLoadMore)
            }
        }
    }
}

@Composable
private fun ColumnScope.AlbumsGrid(
    albums: List<Pair<String, Int>>,
    shown: Int,
    total: Int,
    onLoadMore: () -> Unit,
    songs: List<SongMeta>,
    artwork: ArtworkRepository,
    onOpen: (String) -> Unit
) {
    if (total == 0) {
        Text("No albums yet — tags without an album show under Songs.")
        return
    }
    LazyColumn(modifier = Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items(albums, key = { it.first }) { (name, count) ->
            val firstArtist = songs.firstOrNull { it.album == name }?.artist?.takeIf { it != "Unknown" }
            var url by remember(name) { mutableStateOf<String?>(null) }
            LaunchedEffect(name) { url = artwork.albumArt(name, firstArtist) }
            Row(
                modifier = Modifier.fillMaxWidth().clickable { onOpen(name) },
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                UrlThumb(url = url, size = 56.dp)
                Column(modifier = Modifier.weight(1f)) {
                    Text(name, style = MaterialTheme.typography.bodyMedium)
                    Text(
                        "${firstArtist ?: "Unknown"} · $count songs",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
            }
        }
        if (shown < total) {
            item(key = "show-more") {
                MoreRow(shown = shown, total = total, onLoadMore = onLoadMore)
            }
        }
    }
}

/** Shared "Showing X of Y" footer so long lists page instead of composing all rows. */
@Composable
private fun MoreRow(shown: Int, total: Int, onLoadMore: () -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically
    ) {
        TextButton(onClick = onLoadMore) {
            Text("Show more ($shown of $total)")
        }
    }
}

@Composable
private fun ArtistDetail(
    name: String,
    songs: List<SongMeta>,
    artwork: ArtworkRepository,
    onBack: () -> Unit,
    onPlay: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    var url by remember(name) { mutableStateOf<String?>(null) }
    LaunchedEffect(name) { url = artwork.artistImage(name) }
    var limit by remember(songs) { mutableIntStateOf(LIB_PAGE) }
    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Back", tint = com.musicplayer.android.ui.theme.HathorColors.TextBody)
            }
            UrlThumb(url = url, size = 56.dp)
            Column {
                Text(name, style = MaterialTheme.typography.titleMedium)
                Text("${songs.size} songs", style = MaterialTheme.typography.bodySmall)
            }
        }
        LazyColumn(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(songs.take(limit), key = { it.file }) { song ->
                Row(
                    modifier = Modifier.fillMaxWidth().clickable { onPlay(song) },
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(song.title, style = MaterialTheme.typography.bodyMedium)
                        Text(song.album, style = MaterialTheme.typography.bodySmall)
                    }
                    if (song.file == nowPlayingFile && isPlaying) {
                        VisualizerBars()
                    } else {
                        Text(formatDuration(song.durationSec), style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
            if (limit < songs.size) {
                item(key = "show-more") {
                    MoreRow(shown = limit, total = songs.size, onLoadMore = { limit += LIB_PAGE })
                }
            }
        }
    }
}

@Composable
private fun AlbumDetail(
    name: String,
    songs: List<SongMeta>,
    artwork: ArtworkRepository,
    onBack: () -> Unit,
    onPlay: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    val firstArtist = songs.firstOrNull()?.artist?.takeIf { it != "Unknown" }
    var url by remember(name) { mutableStateOf<String?>(null) }
    LaunchedEffect(name) { url = artwork.albumArt(name, firstArtist) }
    var limit by remember(songs) { mutableIntStateOf(LIB_PAGE) }
    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Back", tint = com.musicplayer.android.ui.theme.HathorColors.TextBody)
            }
            UrlThumb(url = url, size = 56.dp)
            Column {
                Text(name, style = MaterialTheme.typography.titleMedium)
                Text("${firstArtist ?: "Unknown"} · ${songs.size} songs", style = MaterialTheme.typography.bodySmall)
            }
        }
        LazyColumn(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(songs.take(limit), key = { it.file }) { song ->
                Row(
                    modifier = Modifier.fillMaxWidth().clickable { onPlay(song) },
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(song.title, style = MaterialTheme.typography.bodyMedium)
                        Text(song.artist, style = MaterialTheme.typography.bodySmall)
                    }
                    if (song.file == nowPlayingFile && isPlaying) {
                        VisualizerBars()
                    } else {
                        Text(formatDuration(song.durationSec), style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
            if (limit < songs.size) {
                item(key = "show-more") {
                    MoreRow(shown = limit, total = songs.size, onLoadMore = { limit += LIB_PAGE })
                }
            }
        }
    }
}

@Composable
private fun CoverThumb(repo: MetadataRepository, fileName: String) {
    var bitmap by remember(fileName) { mutableStateOf<android.graphics.Bitmap?>(null) }
    val density = LocalDensity.current
    LaunchedEffect(fileName) {
        val px = with(density) { 56.dp.toPx() }.toInt() * 2
        bitmap = repo.coverArt(fileName, px)
    }
    Box(modifier = Modifier.size(56.dp), contentAlignment = Alignment.Center) {
        val bmp = bitmap
        if (bmp != null) {
            Image(bitmap = bmp.asImageBitmap(), contentDescription = "Cover", modifier = Modifier.size(56.dp))
        } else {
            Icon(Icons.Filled.MusicNote, contentDescription = null, tint = com.musicplayer.android.ui.theme.HathorColors.AccentBright, modifier = Modifier.size(32.dp))
        }
    }
}

@Composable
private fun EditSongDialog(
    song: SongMeta,
    repo: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    onPlayNext: () -> Unit,
    onEnqueue: () -> Unit,
    onDismiss: () -> Unit
) {
    var title by remember(song.file) { mutableStateOf(song.title) }
    var artist by remember(song.file) { mutableStateOf(song.artist) }
    var album by remember(song.file) { mutableStateOf(song.album) }
    var year by remember(song.file) { mutableStateOf(song.year) }
    var busy by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    val dialogStatus by repo.status.collectAsState()
    val allPlaylists by playlistRepo.playlists.collectAsState()
    var myIds by remember(song.file) { mutableStateOf<List<Long>>(emptyList()) }
    var lookingUp by remember { mutableStateOf(false) }
    var hits by remember(song.file) { mutableStateOf<List<ArtworkRepository.ItunesHit>>(emptyList()) }
    var lookupMsg by remember(song.file) { mutableStateOf<String?>(null) }
    var pendingCover by remember(song.file) { mutableStateOf<ByteArray?>(null) }
    var toggleError by remember(song.file) { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val pickCover = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch(Dispatchers.IO) {
            try {
                val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() }
                val valid = bytes != null &&
                    android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size) != null
                withContext(Dispatchers.Main) {
                    if (valid) {
                        pendingCover = bytes
                        lookupMsg = "Image staged — Save to apply."
                    } else {
                        lookupMsg = "That file is not a readable image."
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) { lookupMsg = "Read failed: ${e.message}" }
            }
        }
    }

    // Desktop add_playlist modal: which playlists already hold this song.
    LaunchedEffect(song.file) {
        playlistRepo.refresh()
        myIds = playlistRepo.idsForSong(song.file)
    }

    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        // Window glass, not flat grey: dark translucent + faint border.
        modifier = Modifier.border(
            width = 1.dp,
            color = Color(0x1AFFFFFF),
            shape = RoundedCornerShape(24.dp)
        ),
        shape = RoundedCornerShape(24.dp),
        containerColor = Color(0xF216161B),
        title = {
            DialogTitleBar(
                title = if (title.isNotBlank()) title else "Edit metadata",
                onClose = { if (!busy) onDismiss() }
            )
        },
        text = {
            // Scrollable: the old fixed column overflowed the dialog, cutting
            // off Cover/Playlists/Save on small screens.
            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()).heightIn(max = 480.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                // Identity header: cover + title/artist + filename.
                Row(
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    CoverPreview(repo = repo, fileName = song.file, pending = pendingCover, size = 64.dp)
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            title.ifBlank { song.file },
                            style = MaterialTheme.typography.titleSmall,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                        )
                        Text(
                            listOfNotNull(
                                artist.takeIf { it.isNotBlank() && it != "Unknown" },
                                album.takeIf { it.isNotBlank() && it != "Unknown" }
                            ).joinToString(" · ").ifBlank { year },
                            style = MaterialTheme.typography.bodySmall,
                            color = com.musicplayer.android.ui.theme.HathorColors.TextHint,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                        )
                        Text(
                            song.file,
                            style = MaterialTheme.typography.bodySmall,
                            color = com.musicplayer.android.ui.theme.HathorColors.TextHint,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                        )
                    }
                }
                dialogStatus?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                // Tags card.
                Column(
                    modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    DialogSection("Tags")
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        HathorTextField(value = title, onValueChange = { title = it }, label = "Title", modifier = Modifier.weight(1f))
                        HathorTextField(value = artist, onValueChange = { artist = it }, label = "Artist", modifier = Modifier.weight(1f))
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        HathorTextField(value = album, onValueChange = { album = it }, label = "Album", modifier = Modifier.weight(1f))
                        HathorTextField(value = year, onValueChange = { year = it }, label = "Year", modifier = Modifier.weight(1f))
                    }
                }
                // Quick actions: queue, cover pick, iTunes lookup.
                Row(
                    modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    FilledTonalButton(onClick = onPlayNext) {
                        Icon(Icons.Filled.SkipNext, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text(" Next")
                    }
                    FilledTonalButton(onClick = onEnqueue) {
                        Icon(Icons.Filled.PlaylistAdd, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text(" Queue")
                    }
                    FilledTonalButton(
                        onClick = { pickCover.launch("image/*") },
                        enabled = !busy
                    ) {
                        Icon(Icons.Filled.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text(" Cover")
                    }
                    FilledTonalButton(
                        onClick = {
                            lookingUp = true
                            lookupMsg = null
                            scope.launch {
                                hits = artwork.searchSongs(title, artist)
                                lookingUp = false
                                if (hits.isEmpty()) lookupMsg = "No iTunes matches."
                            }
                        },
                        enabled = !lookingUp
                    ) {
                        Icon(Icons.Filled.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                        Text(if (lookingUp) "…" else " Find")
                    }
                }
                if (pendingCover != null) {
                    Text("New cover staged — Save to apply.", style = MaterialTheme.typography.bodySmall)
                }
                lookupMsg?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                hits.forEach { hit ->
                    Row(
                        modifier = Modifier.fillMaxWidth().clickable {
                            title = hit.title
                            artist = hit.artist
                            if (hit.album.isNotBlank()) album = hit.album
                            if (hit.year.isNotBlank()) year = hit.year
                            pendingCover = null
                            lookupMsg = "Filled from iTunes — Save to apply."
                            if (hit.artworkUrl.isNotBlank()) {
                                scope.launch {
                                    val bytes = artwork.fetchBytes(hit.artworkUrl)
                                    if (bytes != null) {
                                        pendingCover = bytes
                                        lookupMsg = "Filled from iTunes (cover ready) — Save to apply."
                                    } else {
                                        lookupMsg = "Fields filled; cover download failed."
                                    }
                                }
                            }
                        }.padding(vertical = 4.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        UrlThumb(url = hit.artworkThumb.ifBlank { null }, size = 40.dp)
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                hit.title,
                                style = MaterialTheme.typography.bodyMedium,
                                maxLines = 1,
                                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                            )
                            Text(
                                listOfNotNull(
                                    hit.artist.ifBlank { null },
                                    hit.album.ifBlank { null },
                                    hit.year.ifBlank { null }
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                maxLines = 1,
                                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                            )
                        }
                    }
                }
                // Playlists card: reachable now that the dialog scrolls; toggle
                // failures surface instead of failing silently.
                Column(
                    modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    DialogSection("Playlists")
                    if (allPlaylists.isEmpty()) {
                        Text("No playlists yet — create one from the Playlists tab.", style = MaterialTheme.typography.bodySmall)
                    }
                    allPlaylists.forEach { pl ->
                        val checked = myIds.contains(pl.id)
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier.fillMaxWidth().clickable {
                                val next = if (checked) myIds - pl.id else myIds + pl.id
                                myIds = next
                                scope.launch {
                                    toggleError = null
                                    if (!playlistRepo.setSongPlaylists(song.file, title, next)) {
                                        toggleError = "Couldn't update playlists — try again."
                                        myIds = playlistRepo.idsForSong(song.file)
                                    }
                                }
                            }
                            .padding(vertical = 2.dp)
                        ) {
                            Checkbox(checked = checked, onCheckedChange = null)
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    pl.title,
                                    style = MaterialTheme.typography.bodyMedium,
                                    maxLines = 1,
                                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                                )
                                Text(
                                    "${pl.songCount} songs",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = com.musicplayer.android.ui.theme.HathorColors.TextHint
                                )
                            }
                        }
                    }
                    toggleError?.let {
                        Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                    }
                }
                if (confirmDelete) {
                    Text("Delete this file + its library rows? This cannot be undone.")
                }
            }
        },
        confirmButton = {
            if (confirmDelete) {
                Button(
                    onClick = {
                        busy = true
                        scope.launch {
                            if (repo.deleteSong(song.file)) onDismiss() else busy = false
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
                ) { Text("Confirm delete") }
            } else {
                Button(
                    onClick = {
                        busy = true
                        scope.launch {
                            // Desktop apply_metadata(): tags + cover land together on Save.
                            val cover = pendingCover
                            if (repo.updateMeta(song.file, title, artist, album, year, removeCover = false)) {
                                if (cover != null) repo.setCoverBytes(song.file, cover, "image/jpeg")
                                onDismiss()
                            } else busy = false
                        }
                    }
                ) { Text("Save") }
            }
        },
        dismissButton = {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (!confirmDelete) {
                    FilledTonalButton(
                        onClick = {
                            busy = true
                            pendingCover = null
                            scope.launch {
                                // 'REMOVE' cover path, like desktop: drop APIC, keep the rest.
                                if (repo.updateMeta(song.file, title, artist, album, year, removeCover = true)) {
                                    onDismiss()
                                } else busy = false
                            }
                        }
                    ) { Text("Remove cover") }
                    FilledTonalButton(
                        onClick = { confirmDelete = true },
                        colors = ButtonDefaults.filledTonalButtonColors(
                            contentColor = MaterialTheme.colorScheme.error
                        )
                    ) { Text("Delete") }
                }
            }
        }
    )
}

private fun formatDuration(sec: Long): String {
    if (sec <= 0) return "--:--"
    return "%d:%02d".format(sec / 60, sec % 60)
}

/** Desktop edit-cover preview: staged image first, else the embedded cover. */
@Composable
private fun CoverPreview(
    repo: MetadataRepository,
    fileName: String,
    pending: ByteArray?,
    size: androidx.compose.ui.unit.Dp = 72.dp
) {
    var current by remember(fileName) { mutableStateOf<android.graphics.Bitmap?>(null) }
    val density = LocalDensity.current
    LaunchedEffect(fileName) {
        val px = with(density) { size.toPx() }.toInt() * 2
        current = repo.coverArt(fileName, px)
    }
    val staged = remember(pending) {
        pending?.let { android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size) }
    }
    val bmp = staged ?: current
    Box(modifier = Modifier.size(size), contentAlignment = Alignment.Center) {
        if (bmp != null) {
            Image(bitmap = bmp.asImageBitmap(), contentDescription = "Cover preview", modifier = Modifier.size(size))
        } else {
            Text("No cover", style = MaterialTheme.typography.bodySmall)
        }
    }
}

/** Small accent section label so the dialog reads grouped, not tabular. */
@Composable
private fun DialogSection(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.titleSmall,
        color = com.musicplayer.android.ui.theme.HathorColors.AccentBright
    )
}
