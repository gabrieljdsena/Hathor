package com.musicplayer.android.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
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
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.PlaylistItem
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.launch

/**
 * Standalone "Add to playlists" modal (web SongPlaylistsModal parity):
 * checkbox membership for one song, immediate saves, and inline playlist
 * creation that also assigns the new playlist.
 *
 * The metadata editor still exposes the same membership toggles; this dialog
 * is the direct action from the song menu.
 */
@Composable
internal fun SongPlaylistsDialog(
    song: SongMeta,
    repo: PlaylistRepository,
    onDismiss: () -> Unit
) {
    val playlists by repo.playlists.collectAsState()
    var myIds by remember(song.file) { mutableStateOf<Set<Long>>(emptySet()) }
    var loading by remember(song.file) { mutableStateOf(true) }
    var newName by remember { mutableStateOf("") }
    var creating by remember { mutableStateOf(false) }
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(song.file) {
        repo.refresh()
        myIds = repo.idsForSong(song.file).toSet()
        loading = false
    }

    fun toggle(pl: PlaylistItem) {
        if (creating || saving) return
        val next = if (pl.id in myIds) myIds - pl.id else myIds + pl.id
        myIds = next
        error = null
        saving = true
        scope.launch {
            try {
                if (!repo.setSongPlaylists(song.file, song.title, next.toList())) {
                    error = "Couldn't update playlists — try again."
                    myIds = repo.idsForSong(song.file).toSet()
                }
            } finally {
                saving = false
            }
        }
    }

    AlertDialog(
        onDismissRequest = { if (!creating) onDismiss() },
        title = { DialogTitleBar(title = "Add to playlists", onClose = onDismiss) },
        text = {
            Column(
                modifier = Modifier
                    .verticalScroll(rememberScrollState())
                    .heightIn(max = 460.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Text(
                    listOfNotNull(
                        song.title,
                        song.artist.takeIf { it.isNotBlank() && it != "Unknown" }
                    ).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )

                if (loading) {
                    Text("Loading playlists…", style = MaterialTheme.typography.bodySmall)
                } else if (playlists.isEmpty()) {
                    Text(
                        "No playlists yet — name one below and it will be created and assigned.",
                        style = MaterialTheme.typography.bodySmall
                    )
                } else {
                    Column(
                        modifier = Modifier.fillMaxWidth().hathorGlass().padding(8.dp),
                        verticalArrangement = Arrangement.spacedBy(2.dp)
                    ) {
                        playlists.forEach { pl ->
                            val checked = pl.id in myIds
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable { toggle(pl) }
                                    .padding(vertical = 2.dp),
                                verticalAlignment = Alignment.CenterVertically
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
                                        color = HathorColors.TextHint
                                    )
                                }
                            }
                        }
                    }
                }

                error?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error
                    )
                }

                Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(6.dp)
                ) {
                    HathorTextField(
                        value = newName,
                        onValueChange = { newName = it },
                        label = "New playlist",
                        modifier = Modifier.fillMaxWidth()
                    )
                    FilledTonalButton(
                        enabled = newName.isNotBlank() && !creating && !saving,
                        onClick = {
                            val name = newName.trim()
                            if (name.isBlank()) return@FilledTonalButton
                            creating = true
                            error = null
                            scope.launch {
                                try {
                                    val id = repo.create(name, null)
                                    val next = myIds + id
                                    if (repo.setSongPlaylists(song.file, song.title, next.toList())) {
                                        myIds = next
                                        newName = ""
                                    } else {
                                        error = "Playlist created, but assigning it failed."
                                        myIds = repo.idsForSong(song.file).toSet()
                                    }
                                } catch (e: Exception) {
                                    error = "Create failed: ${e.message}"
                                } finally {
                                    creating = false
                                }
                            }
                        }
                    ) {
                        Text(if (creating) "Creating…" else "Create + add")
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = onDismiss) { Text("Done") }
        }
    )
}
