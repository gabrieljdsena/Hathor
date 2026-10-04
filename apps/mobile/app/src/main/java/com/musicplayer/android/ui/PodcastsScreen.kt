package com.musicplayer.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Label
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PodcastRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Locale

/**
 * Podcasts destination (desktop podcasts view + sidebar entry): a separate
 * audio library in its own folder with its own Podcasts table. Fully
 * excluded from All Songs, Daily Mix, Recently Played, and music history.
 *
 * Listing is lightweight by design: DB rows + folder scan only (no tag
 * parsing, no covers). Full metadata loads only via the episode menu (Get
 * Metadata), automatically at play time, or via Edit Info.
 */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun PodcastsScreen(
    repo: PodcastRepository,
    metadata: MetadataRepository,
    onPlay: (SongMeta, List<SongMeta>) -> Unit,
    onPlayAll: (List<SongMeta>) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean,
    isCurrentlyPlaying: () -> Boolean = { false },
    onTogglePause: () -> Unit = {}
) {
    val episodes by repo.episodes.collectAsState()
    val tags by repo.tags.collectAsState()
    val tagsByFile by repo.tagsByFile.collectAsState()
    var query by remember { mutableStateOf("") }
    var menuEpisode by remember { mutableStateOf<SongMeta?>(null) }
    var editing by remember { mutableStateOf<SongMeta?>(null) }
    var confirmingDelete by remember { mutableStateOf<SongMeta?>(null) }
    var metaInfo by remember { mutableStateOf<Pair<SongMeta, SongMeta>?>(null) }
    var busy by remember { mutableStateOf(false) }
    // Tag filter (null = all) + tag CRUD/manager dialog states.
    var selectedTagId by remember { mutableStateOf<Long?>(null) }
    var showTagManager by remember { mutableStateOf(false) }
    var tagging by remember { mutableStateOf<SongMeta?>(null) }
    var renamingTag by remember { mutableStateOf<PodcastRepository.PodcastTag?>(null) }
    var deletingTag by remember { mutableStateOf<PodcastRepository.PodcastTag?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(Unit) { repo.refresh() }

    var debouncedQuery by remember { mutableStateOf("") }
    LaunchedEffect(query) {
        delay(300)
        debouncedQuery = query
    }
    // A deleted tag never sticks as a filter.
    val activeTagId = selectedTagId?.takeIf { id -> tags.any { it.id == id } }
    // Queue follows the filter: Play / Play-all queue from THIS list, so a
    // tag-filtered view plays exactly the filtered episodes in order.
    val visible = remember(episodes, debouncedQuery, activeTagId, tagsByFile) {
        val q = debouncedQuery.trim().lowercase()
        episodes.filter { ep ->
            (activeTagId == null || tagsByFile[ep.file]?.contains(activeTagId) == true) &&
                (q.isBlank() || ep.title.lowercase().contains(q) || ep.artist.lowercase().contains(q))
        }
    }

    Column(modifier = Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            HeaderIconTile {
                Icon(Icons.Filled.Mic, contentDescription = null, tint = HathorColors.AccentBright)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text("Podcasts", style = MaterialTheme.typography.titleLarge)
                Text(
                    "${visible.size} episode${if (visible.size == 1) "" else "s"}",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            }
            // Play-all (desktop pod-play-all): toggles when the current
            // episode is in this list, else starts from the first.
            androidx.compose.material3.FilledIconButton(
                onClick = {
                    if (visible.isEmpty()) return@FilledIconButton
                    val fromHere = nowPlayingFile != null &&
                        visible.any { it.file == nowPlayingFile }
                    if (fromHere && isCurrentlyPlaying()) onTogglePause()
                    else onPlayAll(visible)
                },
                enabled = visible.isNotEmpty()
            ) {
                Icon(Icons.Filled.PlayArrow, contentDescription = "Play all")
            }
            IconButton(onClick = { scope.launch { repo.rescan() } }) {
                Icon(Icons.Filled.Refresh, contentDescription = "Sync folder", tint = HathorColors.TextHint)
            }
            IconButton(onClick = { showTagManager = true }) {
                Icon(Icons.Filled.Label, contentDescription = "Manage tags", tint = HathorColors.TextHint)
            }
        }
        HathorTextField(
            value = query,
            onValueChange = { query = it },
            label = "Search episodes…",
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
        // Tag filter chips (same FilterChip style as the download toggle).
        if (tags.isNotEmpty()) {
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                FilterChip(
                    selected = activeTagId == null,
                    onClick = { selectedTagId = null },
                    label = { Text("All (${episodes.size})") }
                )
                tags.forEach { tag ->
                    FilterChip(
                        selected = activeTagId == tag.id,
                        onClick = { selectedTagId = if (activeTagId == tag.id) null else tag.id },
                        label = { Text("${tag.name} (${tag.episodeCount})") }
                    )
                }
            }
        }
        if (visible.isEmpty()) {
            Text(
                "No episodes yet — download podcasts from the Download tab.",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
        }
        LazyColumn(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(visible, key = { it.file }) { ep ->
                val active = ep.file == nowPlayingFile
                Row(
                    modifier = Modifier.fillMaxWidth()
                        .hathorGlass()
                        .then(
                            if (active) Modifier.border(
                                1.dp, HathorColors.Accent.copy(alpha = 0.3f),
                                RoundedCornerShape(16.dp)
                            ) else Modifier
                        )
                        .combinedClickable(
                            // Desktop _podPlay: queue from the VISIBLE list.
                            onClick = { onPlay(ep, visible) },
                            onLongClick = { menuEpisode = ep }
                        )
                        .padding(8.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (active && isPlaying) {
                        VisualizerBars(modifier = Modifier.size(48.dp, 20.dp))
                    } else {
                        Box(
                            modifier = Modifier.size(48.dp).clip(RoundedCornerShape(8.dp)),
                            contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                Icons.Filled.Mic,
                                contentDescription = null,
                                tint = HathorColors.AccentBright,
                                modifier = Modifier.size(28.dp)
                            )
                        }
                    }
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            ep.title.ifBlank { ep.file },
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (active) HathorColors.AccentBright else HathorColors.TextPrimary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        val epTagNames = remember(ep.file, tagsByFile, tags) {
                            val ids = tagsByFile[ep.file] ?: emptySet()
                            if (ids.isEmpty()) null
                            else tags.filter { it.id in ids }.map { it.name }.takeIf { it.isNotEmpty() }
                        }
                        Text(
                            listOfNotNull(
                                ep.artist.takeIf { it.isNotBlank() },
                                ep.dateDownload?.let { shortDate(it) },
                                epTagNames?.joinToString(", ")
                            ).joinToString(" · ").ifBlank { "Episode" },
                            style = MaterialTheme.typography.bodySmall,
                            color = HathorColors.TextHint,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    IconButton(onClick = { menuEpisode = ep }) {
                        Icon(Icons.Filled.MoreVert, contentDescription = "Episode menu", tint = HathorColors.TextHint)
                    }
                }
            }
        }
    }

    // Episode menu (⋮ button + long-press): Play, Play Next, Add to Queue,
    // Get Metadata, Edit Info, Delete (with remote tombstone).
    menuEpisode?.let { ep ->
        AlertDialog(
            onDismissRequest = { menuEpisode = null },
            title = { DialogTitleBar(title = ep.title.ifBlank { ep.file }, onClose = { menuEpisode = null }) },
            text = {
                Text(
                    ep.artist.ifBlank { "Unknown author" },
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            },
            confirmButton = {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Button(
                        onClick = { onPlay(ep, visible); menuEpisode = null },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = HathorColors.Accent, contentColor = Color.White
                        ),
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Play") }
                    OutlinedButton(
                        onClick = { onPlayNext(ep); menuEpisode = null },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Play next") }
                    OutlinedButton(
                        onClick = { onEnqueue(ep); menuEpisode = null },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Add to queue") }
                    OutlinedButton(
                        onClick = {
                            val target = ep
                            menuEpisode = null
                            scope.launch(Dispatchers.IO) {
                                busy = true
                                val full = try {
                                    repo.refreshFromTags(target.file)
                                } catch (_: Exception) {
                                    null
                                }
                                withContext(Dispatchers.Main) {
                                    busy = false
                                    if (full != null) metaInfo = target to full
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Get Metadata") }
                    OutlinedButton(
                        onClick = { editing = ep; menuEpisode = null },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Edit Info") }
                    OutlinedButton(
                        onClick = { tagging = ep; menuEpisode = null },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Tags…") }
                    TextButton(
                        onClick = { confirmingDelete = ep; menuEpisode = null },
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Delete", color = MaterialTheme.colorScheme.error) }
                }
            }
        )
    }

    // Get Metadata result: full tag read for one episode.
    metaInfo?.let { (ep, full) ->
        var cover by remember(ep.file) { mutableStateOf<android.graphics.Bitmap?>(null) }
        LaunchedEffect(ep.file) {
            cover = try {
                withContext(Dispatchers.IO) {
                    val f = java.io.File(repo.dir(), ep.file)
                    if (f.exists()) metadata.coverArtFor(f, 256) else null
                }
            } catch (_: Exception) {
                null
            }
        }
        AlertDialog(
            onDismissRequest = { metaInfo = null },
            title = { DialogTitleBar(title = full.title.ifBlank { ep.file }, onClose = { metaInfo = null }) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    cover?.let { bmp ->
                        androidx.compose.foundation.Image(
                            bitmap = bmp.asImageBitmap(),
                            contentDescription = "Episode cover",
                            modifier = Modifier.size(128.dp).clip(RoundedCornerShape(12.dp))
                        )
                    }
                    Text("Author: ${full.artist.ifBlank { "Unknown" }}", style = MaterialTheme.typography.bodySmall)
                    Text("Album: ${full.album.ifBlank { "Unknown" }}", style = MaterialTheme.typography.bodySmall)
                    Text(
                        "Duration: ${if (full.durationSec > 0) "%d:%02d".format(full.durationSec / 60, full.durationSec % 60) else "unknown"}",
                        style = MaterialTheme.typography.bodySmall
                    )
                }
            },
            confirmButton = {}
        )
    }

    // Edit Info: shared edit path into the podcast folder + Podcasts table.
    editing?.let { ep ->
        var title by remember(ep.file) { mutableStateOf(ep.title) }
        var artist by remember(ep.file) { mutableStateOf(ep.artist) }
        AlertDialog(
            onDismissRequest = { editing = null },
            title = { DialogTitleBar(title = "Edit episode", onClose = { editing = null }) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    HathorTextField(value = title, onValueChange = { title = it }, label = "Title")
                    HathorTextField(value = artist, onValueChange = { artist = it }, label = "Author")
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch(Dispatchers.IO) {
                            repo.updateInfo(ep.file, title.trim(), artist.trim())
                            withContext(Dispatchers.Main) { editing = null }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent, contentColor = Color.White
                    )
                ) { Text("Save") }
            }
        )
    }

    confirmingDelete?.let { ep ->
        AlertDialog(
            onDismissRequest = { confirmingDelete = null },
            title = { DialogTitleBar(title = "Delete episode?", onClose = { confirmingDelete = null }) },
            text = { Text("\"${ep.title.ifBlank { ep.file }}\" will be removed from this device and from the remote library.") },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch(Dispatchers.IO) {
                            repo.delete(ep.file)
                            withContext(Dispatchers.Main) { confirmingDelete = null }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
                ) { Text("Delete") }
            }
        )
    }

    // Per-episode tag assignment (toggles apply immediately, like the
    // song-edit Playlists card).
    tagging?.let { ep ->
        TagAssignDialog(
            episode = ep,
            repo = repo,
            onDismiss = { tagging = null }
        )
    }

    // Tag manager (create/rename/delete, like the Playlists dialogs).
    if (showTagManager) {
        TagManagerDialog(
            repo = repo,
            onRename = { renamingTag = it },
            onDelete = { deletingTag = it },
            onDismiss = { showTagManager = false }
        )
    }

    renamingTag?.let { tag ->
        var name by remember(tag.id) { mutableStateOf(tag.name) }
        var error by remember(tag.id) { mutableStateOf<String?>(null) }
        AlertDialog(
            onDismissRequest = { renamingTag = null },
            title = { DialogTitleBar(title = "Rename tag", onClose = { renamingTag = null }) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    HathorTextField(value = name, onValueChange = { name = it }, label = "Name")
                    error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch(Dispatchers.IO) {
                            val ok = repo.renameTag(tag.id, name)
                            withContext(Dispatchers.Main) {
                                if (ok) renamingTag = null
                                else error = "Name is blank or already used."
                            }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent, contentColor = Color.White
                    )
                ) { Text("Save") }
            }
        )
    }

    deletingTag?.let { tag ->
        AlertDialog(
            onDismissRequest = { deletingTag = null },
            title = { DialogTitleBar(title = "Delete tag?", onClose = { deletingTag = null }) },
            text = { Text("\"${tag.name}\" will be removed from ${tag.episodeCount} episode(s) and from the remote library.") },
            confirmButton = {
                Button(
                    onClick = {
                        scope.launch(Dispatchers.IO) {
                            repo.deleteTag(tag.id)
                            withContext(Dispatchers.Main) {
                                if (selectedTagId == tag.id) selectedTagId = null
                                deletingTag = null
                            }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)
                ) { Text("Delete") }
            }
        )
    }
}

/**
 * Tag manager dialog: glass create card on top, tag pills below (avatar
 * initial + name + count, rename/delete actions). One tap target per row.
 */
@Composable
private fun TagManagerDialog(
    repo: PodcastRepository,
    onRename: (PodcastRepository.PodcastTag) -> Unit,
    onDelete: (PodcastRepository.PodcastTag) -> Unit,
    onDismiss: () -> Unit
) {
    val tags by repo.tags.collectAsState()
    var newName by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { DialogTitleBar(title = "Podcast tags", onClose = onDismiss) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                // Create card.
                Column(
                    modifier = Modifier.fillMaxWidth().hathorGlass().padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    Text("New tag", style = MaterialTheme.typography.titleSmall, color = HathorColors.AccentBright)
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        HathorTextField(
                            value = newName,
                            onValueChange = { newName = it },
                            label = "Name…",
                            modifier = Modifier.weight(1f)
                        )
                        Button(
                            onClick = {
                                scope.launch(Dispatchers.IO) {
                                    val id = repo.createTag(newName)
                                    withContext(Dispatchers.Main) {
                                        if (id != -1L) {
                                            newName = ""
                                            error = null
                                        } else {
                                            error = "Name is blank or already used."
                                        }
                                    }
                                }
                            },
                            colors = ButtonDefaults.buttonColors(
                                containerColor = HathorColors.Accent, contentColor = Color.White
                            )
                        ) { Text("Add") }
                    }
                    error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
                }
                if (tags.isEmpty()) {
                    Text(
                        "No tags yet — create one above, then assign it from an episode's Tags menu.",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint
                    )
                }
                tags.forEach { tag ->
                    TagRow(
                        name = tag.name,
                        caption = "${tag.episodeCount} episode${if (tag.episodeCount == 1) "" else "s"}",
                        onRename = { onRename(tag) },
                        onDelete = { onDelete(tag) }
                    )
                }
            }
        },
        confirmButton = {}
    )
}

/** Shared tag row: accent avatar initial + name/count + rename/delete. */
@Composable
private fun TagRow(
    name: String,
    caption: String,
    onRename: () -> Unit,
    onDelete: () -> Unit
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxWidth().hathorGlass().padding(8.dp)
    ) {
        Box(
            modifier = Modifier.size(40.dp)
                .clip(androidx.compose.foundation.shape.CircleShape)
                .background(HathorColors.AccentFill),
            contentAlignment = Alignment.Center
        ) {
            Text(
                name.take(1).uppercase(),
                style = MaterialTheme.typography.titleSmall,
                color = HathorColors.AccentBright,
                maxLines = 1,
                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
            )
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                name,
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
            )
            Text(caption, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
        IconButton(onClick = onRename) {
            Icon(Icons.Filled.Edit, contentDescription = "Rename tag", tint = HathorColors.TextHint)
        }
        IconButton(onClick = onDelete) {
            Icon(Icons.Filled.Delete, contentDescription = "Delete tag", tint = MaterialTheme.colorScheme.error)
        }
    }
}

/**
 * Per-episode tag assignment: tags as toggle pills (tap to assign /
 * unassign, applied immediately) + inline create-and-assign.
 */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun TagAssignDialog(
    episode: SongMeta,
    repo: PodcastRepository,
    onDismiss: () -> Unit
) {
    val tags by repo.tags.collectAsState()
    val tagsByFile by repo.tagsByFile.collectAsState()
    var error by remember(episode.file) { mutableStateOf<String?>(null) }
    var newName by remember(episode.file) { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    val assigned = tagsByFile[episode.file] ?: emptySet()

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { DialogTitleBar(title = "Tags", onClose = onDismiss) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    episode.title.ifBlank { episode.file },
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
                if (tags.isEmpty()) {
                    Text(
                        "No tags yet — create one below, it assigns right away.",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint
                    )
                } else {
                    androidx.compose.foundation.layout.FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        tags.forEach { tag ->
                            val checked = tag.id in assigned
                            FilterChip(
                                selected = checked,
                                onClick = {
                                    error = null
                                    scope.launch(Dispatchers.IO) {
                                        val ok = if (checked) repo.unassignTag(episode.file, tag.id)
                                        else repo.assignTag(episode.file, tag.id)
                                        if (!ok) withContext(Dispatchers.Main) {
                                            error = "Couldn't update tags — try again."
                                        }
                                    }
                                },
                                label = {
                                    Text(
                                        "${tag.name} (${tag.episodeCount})",
                                        maxLines = 1,
                                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                                    )
                                },
                                leadingIcon = if (checked) {
                                    {
                                        Icon(
                                            Icons.Filled.Check,
                                            contentDescription = null,
                                            modifier = Modifier.size(18.dp)
                                        )
                                    }
                                } else null
                            )
                        }
                    }
                }
                error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
                // Inline create so a tag can be made + assigned in one stop.
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    HathorTextField(
                        value = newName,
                        onValueChange = { newName = it },
                        label = "New tag…",
                        modifier = Modifier.weight(1f)
                    )
                    Button(
                        onClick = {
                            scope.launch(Dispatchers.IO) {
                                val id = repo.createTag(newName)
                                if (id != -1L) {
                                    repo.assignTag(episode.file, id)
                                    withContext(Dispatchers.Main) { newName = "" }
                                } else {
                                    withContext(Dispatchers.Main) {
                                        error = "Name is blank or already used."
                                    }
                                }
                            }
                        },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = HathorColors.Accent, contentColor = Color.White
                        )
                    ) { Text("Add") }
                }
            }
        },
        confirmButton = {}
    )
}

private fun shortDate(raw: String): String {
    return try {
        val parsed = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).parse(raw)
            ?: return raw
        SimpleDateFormat("MMM d, yyyy", Locale.getDefault()).format(parsed)
    } catch (_: Exception) {
        raw
    }
}
