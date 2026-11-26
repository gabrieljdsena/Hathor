@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.musicplayer.android.ui

import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.PlaylistAdd
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PendingMetadataEdit
import com.musicplayer.android.data.PendingMetadataEdits
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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
            FilterChip(
                selected = active,
                onClick = { onPick(key) },
                label = { Text(key, style = MaterialTheme.typography.bodySmall) },
                leadingIcon = if (active) {
                    {
                        Icon(
                            if (ascending) Icons.Filled.ArrowUpward else Icons.Filled.ArrowDownward,
                            contentDescription = if (ascending) "Ascending" else "Descending",
                            tint = com.musicplayer.android.ui.theme.HathorColors.AccentBright,
                            modifier = Modifier.size(14.dp)
                        )
                    }
                } else null,
                colors = FilterChipDefaults.filterChipColors(
                    containerColor = Color.Transparent,
                    labelColor = com.musicplayer.android.ui.theme.HathorColors.TextHint,
                    selectedContainerColor = com.musicplayer.android.ui.theme.HathorColors.AccentFill,
                    selectedLabelColor = com.musicplayer.android.ui.theme.HathorColors.AccentBright
                )
            )
        }
    }
}

@Composable
internal fun EditSongDialog(
    song: SongMeta,
    repo: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    onPlayNext: () -> Unit,
    onEnqueue: () -> Unit,
    onDismiss: () -> Unit,
    playingFile: String? = null
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
                        PendingMetadataEdits.clearFor(song.file)
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
                        val cover = pendingCover
                        if (playingFile == song.file) {
                            // Tag rewrites on the player's open file are deferred
                            // until the track changes (web pending-edit parity).
                            PendingMetadataEdits.stage(
                                PendingMetadataEdit(
                                    file = song.file,
                                    title = title,
                                    artist = artist,
                                    album = album,
                                    year = year,
                                    cover = cover,
                                    removeCover = false
                                )
                            )
                            Toast.makeText(
                                context,
                                "Edit queued — applies when this track changes.",
                                Toast.LENGTH_SHORT
                            ).show()
                            busy = false
                            onDismiss()
                        } else {
                            scope.launch {
                                // Desktop apply_metadata(): tags + cover land together on Save.
                                if (repo.updateMeta(song.file, title, artist, album, year, removeCover = false)) {
                                    if (cover != null) repo.setCoverBytes(song.file, cover, "image/jpeg")
                                    onDismiss()
                                } else busy = false
                            }
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
                            if (playingFile == song.file) {
                                PendingMetadataEdits.stage(
                                    PendingMetadataEdit(
                                        file = song.file,
                                        title = title,
                                        artist = artist,
                                        album = album,
                                        year = year,
                                        cover = null,
                                        removeCover = true
                                    )
                                )
                                Toast.makeText(
                                    context,
                                    "Cover removal queued — applies when this track changes.",
                                    Toast.LENGTH_SHORT
                                ).show()
                                busy = false
                                onDismiss()
                            } else {
                                scope.launch {
                                    // 'REMOVE' cover path, like desktop: drop APIC, keep the rest.
                                    if (repo.updateMeta(song.file, title, artist, album, year, removeCover = true)) {
                                        onDismiss()
                                    } else busy = false
                                }
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
