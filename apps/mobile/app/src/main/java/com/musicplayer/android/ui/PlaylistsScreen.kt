@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
package com.musicplayer.android.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.Base64
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PlaylistItem
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.hathorGlass
import com.musicplayer.android.util.FuzzySearch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File

/** Rows rendered before "Show more" — playlists can hold hundreds of songs. */
private const val PLAYLIST_PAGE = 50

/**
 * Playlists destination (desktop playlist_home + playlist_view + add_playlist
 * modal). List with counts, create/rename/delete, detail with songs, play-all,
 * per-song add/remove, cover from the gallery (stored as a data URI, like the
 * desktop thumbnail field).
 */
@Composable
fun PlaylistsScreen(
    repo: PlaylistRepository,
    metadata: MetadataRepository,
    artwork: ArtworkRepository,
    musicDir: File,
    nowPlayingFile: String?,
    onPlayFiles: (List<File>, Long) -> Unit,
    onPlaySong: (SongMeta, List<SongMeta>, Long) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit
) {
    val playlists by repo.playlists.collectAsState()
    var creating by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<PlaylistItem?>(null) }
    var deleting by remember { mutableStateOf<PlaylistItem?>(null) }
    var opened by remember { mutableStateOf<PlaylistItem?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) { repo.refresh() }

    opened?.let { pl ->
        PlaylistDetail(
            repo = repo,
            metadata = metadata,
            artwork = artwork,
            playlist = pl,
            musicDir = musicDir,
            nowPlayingFile = nowPlayingFile,
            onBack = { opened = null; scope.launch { repo.refresh() } },
            onPlayFiles = onPlayFiles,
            onPlaySong = onPlaySong,
            onPlayNext = onPlayNext,
            onEnqueue = onEnqueue,
            onDeleted = { opened = null }
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Column {
                Text("Playlists", style = MaterialTheme.typography.titleLarge)
                Text("Your custom playlists", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            }
            Button(
                onClick = { creating = true },
                colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = androidx.compose.ui.graphics.Color.White)
            ) {
                Icon(Icons.Filled.Add, contentDescription = null)
                Text(" New")
            }
        }
        if (playlists.isEmpty()) {
            Text("No playlists yet. Create one, or sync to pull them from the server.")
        }
        // weight(1f) is what makes this list scrollable: without it the
        // LazyColumn has no bounded height inside the outer Column and the
        // rows either overflow or never scroll.
        LazyColumn(
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(playlists, key = { it.id }) { pl ->
                Row(
                    modifier = Modifier.fillMaxWidth().hathorGlass().animateItemPlacement().clickable { opened = pl }.padding(8.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    PlaylistThumb(dataUri = pl.thumbnail)
                    Column(modifier = Modifier.weight(1f)) {
                        Text(pl.title, style = MaterialTheme.typography.bodyMedium)
                        Text(
                            "${pl.songCount} songs${if (!pl.description.isNullOrBlank()) " · ${pl.description}" else ""}",
                            style = MaterialTheme.typography.bodySmall
                        )
                    }
                    IconButton(onClick = { renaming = pl }) {
                        Icon(Icons.Filled.Edit, contentDescription = "Rename", tint = HathorColors.TextHint)
                    }
                    IconButton(onClick = { deleting = pl }) {
                        Icon(Icons.Filled.Delete, contentDescription = "Delete", tint = MaterialTheme.colorScheme.error)
                    }
                }
            }
        }
    }

    if (creating) {
        var title by remember { mutableStateOf("") }
        var desc by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { creating = false },
            title = { DialogTitleBar(title = "New playlist", onClose = { creating = false }) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    HathorTextField(value = title, onValueChange = { title = it }, label = "Title")
                    HathorTextField(value = desc, onValueChange = { desc = it }, label = "Description (optional)")
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch {
                            if (title.isNotBlank()) repo.create(title.trim(), desc.trim().ifBlank { null })
                            creating = false
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = androidx.compose.ui.graphics.Color.White)
                ) { Text("Create") }
            }
        )
    }

    renaming?.let { pl ->
        var title by remember(pl.id) { mutableStateOf(pl.title) }
        var desc by remember(pl.id) { mutableStateOf(pl.description ?: "") }
        AlertDialog(
            onDismissRequest = { renaming = null },
            title = { DialogTitleBar(title = "Rename playlist", onClose = { renaming = null }) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    HathorTextField(value = title, onValueChange = { title = it }, label = "Title")
                    HathorTextField(value = desc, onValueChange = { desc = it }, label = "Description")
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch {
                            if (title.isNotBlank()) repo.rename(pl.id, title.trim(), desc.trim().ifBlank { null })
                            renaming = null
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = androidx.compose.ui.graphics.Color.White)
                ) { Text("Save") }
            }
        )
    }

    deleting?.let { pl ->
        AlertDialog(
            onDismissRequest = { deleting = null },
            title = { DialogTitleBar(title = "Delete playlist?", onClose = { deleting = null }) },
            text = { Text("\"${pl.title}\" and its song links will be removed. Files stay on device.") },
            confirmButton = {
                Button(
                    onClick = { scope.launch { repo.delete(pl.id); deleting = null } },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
                ) { Text("Delete") }
            }
        )
    }
}

@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
private fun PlaylistDetail(
    repo: PlaylistRepository,
    metadata: MetadataRepository,
    artwork: ArtworkRepository,
    playlist: PlaylistItem,
    musicDir: File,
    nowPlayingFile: String?,
    onBack: () -> Unit,
    onPlayFiles: (List<File>, Long) -> Unit,
    onPlaySong: (SongMeta, List<SongMeta>, Long) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    onDeleted: () -> Unit
) {
    var songs by remember(playlist.id) { mutableStateOf<List<SongMeta>>(emptyList()) }
    var thumbnail by remember(playlist.id) { mutableStateOf(playlist.thumbnail) }
    var coverMsg by remember { mutableStateOf<String?>(null) }
    var pvQuery by remember { mutableStateOf("") }
    var pvSort by remember { mutableStateOf<String?>(null) }
    var pvAsc by remember { mutableStateOf(true) }
    var pvLimit by remember { mutableIntStateOf(PLAYLIST_PAGE) }
    // Menu snapshot: song + the visible list at open time (queue context).
    var menuSong by remember { mutableStateOf<Pair<SongMeta, List<SongMeta>>?>(null) }
    var editSong by remember { mutableStateOf<SongMeta?>(null) }
    var playlistSong by remember { mutableStateOf<SongMeta?>(null) }
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    suspend fun reload() {
        songs = repo.songsIn(playlist.id, musicDir)
        pvLimit = PLAYLIST_PAGE
    }
    LaunchedEffect(playlist.id) { reload() }

    val pickCover = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch(Dispatchers.IO) {
            try {
                val bytes = downscaleCover(context, uri)
                if (bytes == null) {
                    coverMsg = "Could not read image"
                } else {
                    val dataUri = "data:image/jpeg;base64," + Base64.encodeToString(bytes, Base64.NO_WRAP)
                    repo.setThumbnail(playlist.id, dataUri)
                    withContext(Dispatchers.Main) {
                        thumbnail = dataUri
                        coverMsg = "Cover updated"
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    coverMsg = "Cover failed: ${e.message}"
                }
            }
        }
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            IconButton(onClick = onBack) {
                Icon(Icons.Filled.ArrowBack, contentDescription = "Back", tint = HathorColors.TextBody)
            }
            Box(modifier = Modifier.clickable { pickCover.launch("image/*") }) {
                PlaylistThumb(dataUri = thumbnail, size = 56.dp)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(playlist.title, style = MaterialTheme.typography.titleMedium)
                Text("${songs.size} songs · tap cover to change it", style = MaterialTheme.typography.bodySmall)
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (thumbnail != null) {
                TextButton(
                    onClick = {
                        scope.launch {
                            repo.setThumbnail(playlist.id, null)
                            withContext(Dispatchers.Main) {
                                thumbnail = null
                                coverMsg = "Cover removed"
                            }
                        }
                    }
                ) { Text("Remove cover") }
            }
            coverMsg?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }
        // Desktop playlist search + column sorts (_pvSortColumn).
        HathorTextField(
            value = pvQuery,
            onValueChange = { pvQuery = it },
            label = "Search in playlist…",
            modifier = Modifier.fillMaxWidth()
        )
        SortHeader(
            keys = listOf("Title", "Album", "Duration", "Added"),
            sortKey = pvSort,
            ascending = pvAsc,
            onPick = { key ->
                if (pvSort == key) pvAsc = !pvAsc
                else {
                    pvSort = key
                    pvAsc = true
                }
            }
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = {
                    val files = songs.map { File(musicDir, it.file) }
                    if (files.isNotEmpty()) onPlayFiles(files, playlist.id)
                },
                enabled = songs.isNotEmpty(),
                colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = androidx.compose.ui.graphics.Color.White)
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = null)
                Text(" Play all")
            }
            TextButton(onClick = { scope.launch { if (repo.delete(playlist.id)) onDeleted() } }) {
                Text("Delete playlist", color = MaterialTheme.colorScheme.error)
            }
        }
        val pq = pvQuery.trim()
        val filtered = if (pq.isBlank()) songs else songs.filter {
            FuzzySearch.matchesFields(listOf(it.title, it.artist), pq)
        }
        val ordered = when (pvSort) {
            "Title" -> filtered.sortedBy { it.title.lowercase() }
            "Album" -> filtered.sortedBy { it.album.lowercase() }
            "Duration" -> filtered.sortedBy { it.durationSec }
            "Added" -> filtered.sortedBy { it.dateAdded ?: "" }
            else -> filtered
        }.let { if (pvSort != null && !pvAsc) it.reversed() else it }
        // Reset paging whenever the visible set changes.
        androidx.compose.runtime.LaunchedEffect(pq, pvSort, pvAsc, songs) { pvLimit = PLAYLIST_PAGE }
        LazyColumn(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(ordered.take(pvLimit), key = { it.file }) { song ->
                Row(
                    modifier = Modifier.fillMaxWidth().heightIn(min = 64.dp).hathorGlass()
                        // Desktop playlist row: queue from the playlist songs
                        // with the playlist id; long-press opens the menu.
                        .combinedClickable(
                            onClick = { onPlaySong(song, ordered, playlist.id) },
                            onLongClick = { menuSong = song to ordered }
                        )
                        .padding(8.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    SongCover(repo = metadata, fileName = song.file, size = 44.dp)
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            song.title,
                            style = MaterialTheme.typography.bodyMedium,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        Text(
                            song.artist,
                            style = MaterialTheme.typography.bodySmall,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    IconButton(
                        onClick = {
                            scope.launch {
                                repo.removeSong(playlist.id, song.file)
                                withContext(Dispatchers.Main) { reload() }
                            }
                        },
                        modifier = Modifier.size(40.dp)
                    ) {
                        Icon(Icons.Filled.Delete, contentDescription = "Remove", tint = HathorColors.TextHint)
                    }
                }
            }
            if (pvLimit < ordered.size) {
                item(key = "show-more") {
                    TextButton(onClick = { pvLimit += PLAYLIST_PAGE }, modifier = Modifier.fillMaxWidth()) {
                        Text("Show more ($pvLimit of ${ordered.size})")
                    }
                }
            }
        }
    }

    menuSong?.let { (song, visible) ->
        SongMenuDialog(
            song = song,
            visible = visible,
            source = com.musicplayer.android.playback.PlaybackSource("playlist", playlist.id.toString()),
            onPlay = { s, v, src ->
                onPlaySong(s, v, src.id?.toLongOrNull() ?: playlist.id)
            },
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
            repo = metadata,
            playlistRepo = repo,
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
            repo = repo,
            onDismiss = { playlistSong = null }
        )
    }
}

@Composable
fun PlaylistThumb(dataUri: String?, size: androidx.compose.ui.unit.Dp = 48.dp) {
    var bitmap by remember(dataUri) { mutableStateOf<android.graphics.Bitmap?>(null) }
    val density = LocalDensity.current
    LaunchedEffect(dataUri) {
        bitmap = dataUri?.let { decodeDataUri(it, with(density) { size.toPx() }.toInt() * 2) }
    }
    Box(modifier = Modifier.size(size), contentAlignment = Alignment.Center) {
        val bmp = bitmap
        if (bmp != null) {
            Image(bitmap = bmp.asImageBitmap(), contentDescription = "Playlist cover", modifier = Modifier.size(size))
        } else {
            Icon(Icons.Filled.MusicNote, contentDescription = null, tint = HathorColors.AccentBright, modifier = Modifier.size(size.times(0.6f)))
        }
    }
}

private fun decodeDataUri(uri: String, reqWidthPx: Int = 0): android.graphics.Bitmap? {
    return try {
        val base64 = uri.substringAfter(",", "")
        if (base64.isEmpty()) return null
        val bytes = Base64.decode(base64, Base64.DEFAULT)
        ImageLoader.decodeSampled(bytes, reqWidthPx)
    } catch (_: Exception) {
        null
    }
}

/** Covers are stored as data URIs, so cap them like the web client (800px JPEG). */
private const val PLAYLIST_COVER_MAX_PX = 800

private fun downscaleCover(
    context: Context,
    uri: Uri,
    maxPx: Int = PLAYLIST_COVER_MAX_PX
): ByteArray? {
    val original = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return null
    return downscaleCoverBytes(original, maxPx)
}

/** Pure-ish decode/scale/JPEG encode so it can be covered by a JVM test. */
internal fun downscaleCoverBytes(
    original: ByteArray,
    maxPx: Int = PLAYLIST_COVER_MAX_PX
): ByteArray? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(original, 0, original.size, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null

    var sample = 1
    while (bounds.outWidth / (sample * 2) >= maxPx || bounds.outHeight / (sample * 2) >= maxPx) {
        sample *= 2
    }
    val bitmap = BitmapFactory.decodeByteArray(
        original,
        0,
        original.size,
        BitmapFactory.Options().apply { inSampleSize = sample }
    ) ?: return null

    val scale = minOf(1f, maxPx.toFloat() / maxOf(bitmap.width, bitmap.height))
    val scaled = if (scale < 1f) {
        Bitmap.createScaledBitmap(
            bitmap,
            (bitmap.width * scale).toInt().coerceAtLeast(1),
            (bitmap.height * scale).toInt().coerceAtLeast(1),
            true
        )
    } else {
        bitmap
    }
    return try {
        val out = ByteArrayOutputStream()
        if (scaled.compress(Bitmap.CompressFormat.JPEG, 85, out)) out.toByteArray() else null
    } finally {
        if (scaled !== bitmap) scaled.recycle()
        bitmap.recycle()
    }
}
