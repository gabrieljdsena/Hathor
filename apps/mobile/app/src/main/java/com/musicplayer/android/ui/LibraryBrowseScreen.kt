package com.musicplayer.android.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Album
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlaybackSource
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.hathorGlass
import com.musicplayer.android.util.FuzzySearch

/**
 * Artists and Albums browse views (web `views/Library.tsx` parity).
 *
 * Both are opened from Home and keep their own list → detail state, matching
 * the mobile back-stack pattern (no router). Artwork is fetched lazily from
 * iTunes per visible row/header and cached by ArtworkRepository.
 */

@Composable
fun ArtistsScreen(
    library: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    onBack: () -> Unit,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    val songs by library.library.collectAsState()
    var query by remember { mutableStateOf("") }
    var openArtist by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) { library.refreshLibrary() }

    val groups = remember(songs) {
        songs.groupBy { it.artist.trim().ifBlank { "Unknown" } }
            .toSortedMap(String.CASE_INSENSITIVE_ORDER)
    }
    val visible = remember(groups, query) {
        val q = query.trim()
        if (q.isBlank()) groups.toList()
        else groups.filterKeys { FuzzySearch.matches(it, q) }.toList()
    }

    openArtist?.let { name ->
        val artistSongs = groups[name].orEmpty()
            .sortedWith(compareBy({ it.album.lowercase() }, { it.title.lowercase() }))
        GroupDetail(
            title = name,
            subtitle = "${artistSongs.size} songs",
            groupKey = "artist:$name",
            artworkLoader = { artwork.artistImage(name) },
            songs = artistSongs,
            source = PlaybackSource("artist", name),
            library = library,
            playlistRepo = playlistRepo,
            artwork = artwork,
            onBack = { openArtist = null },
            onPlay = onPlay,
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Home", tint = HathorColors.TextBody)
            }
            HeaderIconTile {
                Icon(Icons.Filled.Person, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column {
                Text("Artists", style = MaterialTheme.typography.titleLarge)
                Text("${groups.size} artists", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }
        }
        HathorTextField(
            value = query,
            onValueChange = { query = it },
            label = "Search artists…",
            modifier = Modifier.fillMaxWidth()
        )
        if (visible.isEmpty()) {
            Text("No artists match.", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(visible, key = { it.first }) { (name, list) ->
                GroupRow(
                    title = name,
                    subtitle = "${list.size} songs",
                    artworkKey = "artist:$name",
                    artworkLoader = { artwork.artistImage(name) },
                    onClick = { openArtist = name }
                )
            }
        }
    }
}

@Composable
fun AlbumsScreen(
    library: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    onBack: () -> Unit,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    val songs by library.library.collectAsState()
    var query by remember { mutableStateOf("") }
    var openAlbum by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) { library.refreshLibrary() }

    val groups = remember(songs) {
        songs.groupBy { it.album.trim().ifBlank { "Unknown" } }
            .toSortedMap(String.CASE_INSENSITIVE_ORDER)
    }
    val visible = remember(groups, query) {
        val q = query.trim()
        if (q.isBlank()) groups.toList()
        else groups.filterKeys { FuzzySearch.matches(it, q) }.toList()
    }

    openAlbum?.let { title ->
        val albumSongs = groups[title].orEmpty()
            .sortedWith(compareBy({ it.artist.lowercase() }, { it.title.lowercase() }))
        val artists = albumSongs.map { it.artist.trim().ifBlank { "Unknown" } }.distinct()
        val artistLabel = when {
            artists.isEmpty() -> "Unknown artist"
            artists.size == 1 -> artists.first()
            else -> "Various artists"
        }
        GroupDetail(
            title = title,
            subtitle = "$artistLabel · ${albumSongs.size} songs",
            groupKey = "album:$title",
            artworkLoader = { artwork.albumArt(title, artists.singleOrNull()) },
            songs = albumSongs,
            source = PlaybackSource("album", title),
            library = library,
            playlistRepo = playlistRepo,
            artwork = artwork,
            onBack = { openAlbum = null },
            onPlay = onPlay,
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Home", tint = HathorColors.TextBody)
            }
            HeaderIconTile {
                Icon(Icons.Filled.Album, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column {
                Text("Albums", style = MaterialTheme.typography.titleLarge)
                Text("${groups.size} albums", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }
        }
        HathorTextField(
            value = query,
            onValueChange = { query = it },
            label = "Search albums…",
            modifier = Modifier.fillMaxWidth()
        )
        if (visible.isEmpty()) {
            Text("No albums match.", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(visible, key = { it.first }) { (title, list) ->
                val artists = remember(list) {
                    list.map { it.artist.trim().ifBlank { "Unknown" } }.distinct()
                }
                GroupRow(
                    title = title,
                    subtitle = when {
                        artists.isEmpty() -> "${list.size} songs"
                        artists.size == 1 -> "${artists.first()} · ${list.size} songs"
                        else -> "Various artists · ${list.size} songs"
                    },
                    artworkKey = "album:$title",
                    artworkLoader = { artwork.albumArt(title, artists.singleOrNull()) },
                    onClick = { openAlbum = title }
                )
            }
        }
    }
}

@Composable
private fun GroupRow(
    title: String,
    subtitle: String,
    artworkKey: String,
    artworkLoader: suspend () -> String?,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth().hathorGlass().clickable(onClick = onClick).padding(10.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        RemoteArt(key = artworkKey, size = 52.dp, load = artworkLoader)
        Column(modifier = Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyMedium, maxLines = 1)
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
        Icon(Icons.Filled.ChevronRight, contentDescription = null, tint = HathorColors.TextHint)
    }
}

@Composable
private fun GroupDetail(
    title: String,
    subtitle: String,
    groupKey: String,
    artworkLoader: suspend () -> String?,
    songs: List<SongMeta>,
    source: PlaybackSource,
    library: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    onBack: () -> Unit,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    var menuSong by remember { mutableStateOf<SongMeta?>(null) }
    var editSong by remember { mutableStateOf<SongMeta?>(null) }
    var playlistSong by remember { mutableStateOf<SongMeta?>(null) }
    val listState = rememberLazyListState()

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Back", tint = HathorColors.TextBody)
            }
            RemoteArt(key = groupKey, size = 72.dp, load = artworkLoader)
            Column(modifier = Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleLarge, maxLines = 2)
                Text(subtitle, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = { songs.firstOrNull()?.let { onPlay(it, songs, source) } },
                enabled = songs.isNotEmpty(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = null)
                Text(" Play all")
            }
            OutlinedButton(
                onClick = {
                    val shuffled = songs.shuffled()
                    shuffled.firstOrNull()?.let { onPlay(it, shuffled, source) }
                },
                enabled = songs.isNotEmpty()
            ) {
                Icon(Icons.Filled.Shuffle, contentDescription = null)
                Text(" Shuffle")
            }
        }
        LazyColumn(
            state = listState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(songs, key = { it.file }) { song ->
                SongRow(
                    song = song,
                    repo = library,
                    isActive = song.file == nowPlayingFile,
                    isPlaying = isPlaying,
                    onClick = { onPlay(song, songs, source) },
                    onMenu = { menuSong = song }
                )
            }
        }
    }

    menuSong?.let { song ->
        SongMenuDialog(
            song = song,
            visible = songs,
            source = source,
            onPlay = { s, v, src -> onPlay(s, v, src) },
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            onOpenArtist = {},
            onOpenAlbum = {},
            showNav = false,
            onDismiss = { menuSong = null },
            onEdit = { editSong = song },
            onPlaylists = { playlistSong = song }
        )
    }

    editSong?.let { song ->
        EditSongDialog(
            song = song,
            repo = library,
            playlistRepo = playlistRepo,
            artwork = artwork,
            onPlayNext = { onPlayNext(song); editSong = null },
            onEnqueue = { onEnqueue(song); editSong = null },
            onDismiss = { editSong = null; library.clearStatus() },
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
private fun RemoteArt(key: String, size: Dp, load: suspend () -> String?) {
    var url by remember(key) { mutableStateOf<String?>(null) }
    LaunchedEffect(key) {
        url = try {
            load()
        } catch (_: Exception) {
            null
        }
    }
    UrlThumb(url = url, size = size)
}
