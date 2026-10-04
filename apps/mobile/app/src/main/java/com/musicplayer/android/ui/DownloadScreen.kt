@file:OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
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
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.JobItem
import com.musicplayer.android.data.QueueRepository
import com.musicplayer.android.engine.DownloadEngine
import com.musicplayer.android.engine.YtResult
import com.musicplayer.android.playback.PreviewPlayer
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/** Job rows composed per page — the queue log can grow unbounded. */
private const val JOB_PAGE = 50

/**
 * Download destination (desktop download_songs view): URL enqueneueues
 * directly; otherwise a YouTube search shows desktop-style pickable results
 * (desktop search_yt: 5 hits with title/uploader/duration/thumbnail), and the
 * picked hit enqueues with its title/uploader attached. Job log below with
 * per-job progress, retry, and cancel.
 */
@Composable
fun DownloadScreen(
    queue: QueueRepository,
    engine: DownloadEngine,
    preview: PreviewPlayer,
    onPreviewStarted: () -> Unit = {}
) {
    val jobs by queue.jobs.collectAsState()
    val total by queue.total.collectAsState()
    val active by queue.activeCount.collectAsState()
    val previewState by preview.state.collectAsState()
    var input by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var searching by remember { mutableStateOf(false) }
    var results by remember { mutableStateOf<List<YtResult>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // Songs / Podcast toggle: podcast downloads skip the iTunes song-match
    // (keeps uploader title/author, no artwork fetch) and land in the
    // podcasts folder; retries preserve the flag via the stored job row.
    var podcastMode by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    // Load-more pagination for the queue log (same as song lists): only the
    // first pages are composed; scrolling near the end appends the next page.
    var jobLimit by remember { mutableIntStateOf(JOB_PAGE) }
    val pagedJobs = remember(jobs, jobLimit) { jobs.take(jobLimit) }
    val jobListState = rememberLazyListState()
    LaunchedEffect(jobListState, pagedJobs.size, jobs.size, jobLimit) {
        snapshotFlow { jobListState.layoutInfo.visibleItemsInfo.lastOrNull()?.index }
            .collectLatest { lastVisible ->
                if (lastVisible != null && lastVisible >= pagedJobs.size - 8 && jobLimit < jobs.size) {
                    jobLimit += JOB_PAGE
                }
            }
    }

    LaunchedEffect(Unit) { queue.refresh() }
    // Stop any preview when leaving the screen (no orphaned audio).
    DisposableEffect(Unit) { onDispose { preview.stop() } }
    // Surface preview failures in the screen error line.
    LaunchedEffect(previewState) {
        val s = previewState
        if (s is PreviewPlayer.State.Failed) error = "Preview failed: ${s.message}"
    }

    val isUrl = input.trim().startsWith("http", ignoreCase = true)

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text(
            if (podcastMode) "Download Podcasts" else "Download Songs",
            style = MaterialTheme.typography.titleLarge
        )
        Text("Search & download", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            androidx.compose.material3.FilterChip(
                selected = !podcastMode,
                onClick = { podcastMode = false },
                label = { Text("Songs") }
            )
            androidx.compose.material3.FilterChip(
                selected = podcastMode,
                onClick = { podcastMode = true },
                label = { Text("Podcasts") }
            )
        }
        HathorTextField(
            value = input,
            onValueChange = { input = it },
            label = "YouTube URL or search text",
            modifier = Modifier.fillMaxWidth()
        )
        Button(
            onClick = {
                error = null
                val text = input.trim()
                if (text.isEmpty()) {
                    error = "Type a URL or search text first."
                    return@Button
                }
                if (isUrl) {
                    busy = true
                    scope.launch {
                        try {
                            queue.submit(text, "", null, podcastMode)
                            input = ""
                        } catch (e: Exception) {
                            error = e.message
                        }
                        busy = false
                    }
                } else {
                    searching = true
                    results = null
                    scope.launch {
                        val r = engine.searchYouTube(text, 5)
                        r.fold(
                            onSuccess = {
                                results = it
                                if (it.isEmpty()) error = "No results for \"$text\"."
                            },
                            onFailure = { error = it.message }
                        )
                        searching = false
                    }
                }
            },
            enabled = !busy && !searching && input.isNotBlank(),
            modifier = Modifier.fillMaxWidth(),
            colors = ButtonDefaults.buttonColors(containerColor = HathorColors.Accent, contentColor = Color.White)
        ) {
            Text(
                when {
                    busy -> "Queueing…"
                    searching -> "Searching…"
                    isUrl -> "Enqueue download"
                    else -> "Search"
                }
            )
        }
        error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }

        results?.let { hits ->
            Text("Results (${hits.size}) — tap to enqueue, ▶ to preview:", style = MaterialTheme.typography.titleSmall)
        }
        androidx.compose.animation.AnimatedVisibility(
            visible = !results.isNullOrEmpty(),
            enter = androidx.compose.animation.expandVertically() + androidx.compose.animation.fadeIn(),
            exit = androidx.compose.animation.shrinkVertically() + androidx.compose.animation.fadeOut()
        ) {
            val hits = results ?: emptyList()
            LazyColumn(modifier = Modifier.weight(1f, fill = false), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(hits, key = { it.id }) { hit ->
                    Row(
                        modifier = Modifier.fillMaxWidth().hathorGlass()
                            .clickable {
                                scope.launch {
                                    queue.submit(hit.url, hit.title, hit.uploader.ifBlank { null }, podcastMode)
                                    results = null
                                    input = ""
                                }
                            }
                            .padding(8.dp),
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        UrlThumb(url = hit.thumbnail.ifBlank { null }, size = 64.dp)
                        Column(modifier = Modifier.weight(1f)) {
                            Text(hit.title, style = MaterialTheme.typography.bodyMedium, maxLines = 2)
                            Text(
                                "${hit.uploader} · ${formatDuration(hit.durationSec)}",
                                style = MaterialTheme.typography.bodySmall
                            )
                        }
                        PreviewButton(
                            hitId = hit.id,
                            state = previewState,
                            onToggle = { preview.toggle(hit.id, hit.url, engine, onPreviewStarted) }
                        )
                    }
                }
            }
        }

        Text(
            when {
                active > 0 -> "Queue ($active active)"
                total > 0 -> "Queue ($total finished)"
                else -> "Queue (empty)"
            },
            style = MaterialTheme.typography.titleSmall
        )
        TextButton(
            onClick = {
                scope.launch {
                    val n = queue.clearCompleted()
                    error = if (n > 0) "Cleared $n finished job(s)." else null
                }
            }
        ) { Text("Clear completed") }
        LazyColumn(
            state = jobListState,
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            items(pagedJobs, key = { it.qid }) { job ->
                JobRow(job = job, onRetry = { scope.launch { queue.retry(job.qid) } }, onCancel = { scope.launch { queue.cancel(job.qid) } })
            }
            if (pagedJobs.size < jobs.size) {
                item(key = "loading-more") {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                        horizontalArrangement = Arrangement.Center,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        CircularProgressIndicator(
                            modifier = Modifier.padding(end = 8.dp),
                            strokeWidth = 2.dp
                        )
                        Text(
                            "Showing ${pagedJobs.size} of ${jobs.size}…",
                            style = MaterialTheme.typography.bodySmall,
                            color = HathorColors.TextHint
                        )
                    }
                }
            }
        }
    }
}

/**
 * Stream-preview toggle for a search result: play while resolving, stop
 * while playing, play otherwise. Resolving takes a few seconds (yt-dlp).
 */
@Composable
private fun PreviewButton(hitId: String, state: PreviewPlayer.State, onToggle: () -> Unit) {
    val mine = when (state) {
        is PreviewPlayer.State.Loading -> state.id == hitId
        is PreviewPlayer.State.Playing -> state.id == hitId
        is PreviewPlayer.State.Failed -> state.id == hitId
        else -> false
    }
    when {
        state is PreviewPlayer.State.Loading && mine -> CircularProgressIndicator(
            modifier = Modifier.size(24.dp),
            color = HathorColors.AccentBright,
            strokeWidth = 2.dp
        )
        state is PreviewPlayer.State.Playing && mine -> IconButton(onClick = onToggle) {
            Icon(Icons.Filled.Stop, contentDescription = "Stop preview", tint = HathorColors.AccentBright)
        }
        else -> IconButton(onClick = onToggle) {
            Icon(Icons.Filled.PlayArrow, contentDescription = "Preview", tint = HathorColors.TextHint)
        }
    }
}

@Composable
private fun JobRow(job: JobItem, onRetry: () -> Unit, onCancel: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxWidth().hathorGlass().padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    job.title.ifBlank { job.url ?: "" },
                    style = MaterialTheme.typography.bodyMedium,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
                Text(
                    (if (job.isPodcast) "[podcast] " else "") + jobStatusText(job),
                    style = MaterialTheme.typography.bodySmall,
                    color = if (job.status == "failed") MaterialTheme.colorScheme.error else HathorColors.TextHint
                )
            }
            if (job.status == "failed") {
                IconButton(onClick = onRetry) {
                    Icon(Icons.Filled.Refresh, contentDescription = "Retry", tint = HathorColors.AccentBright)
                }
            } else if (job.status == "queued" || job.status == "downloading") {
                IconButton(onClick = onCancel) {
                    Icon(Icons.Filled.Close, contentDescription = "Cancel", tint = HathorColors.TextHint)
                }
            }
        }
        if (job.status == "downloading") {
            val pct01 = (job.progress01.takeIf { it.isFinite() } ?: 0f).coerceIn(0f, 1f)
            LinearProgressIndicator(
                progress = { pct01 },
                modifier = Modifier.fillMaxWidth(),
                color = HathorColors.Accent
            )
        }
        if (job.status == "failed" && !job.error.isNullOrBlank()) {
            Text(job.error, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        }
    }
}

private fun jobStatusText(job: JobItem): String = when (job.status) {
    "queued" -> "queued"
    "downloading" -> {
        // Defensive clamp: progress01 is 0..1, never show >100%.
        val pct = ((job.progress01.takeIf { it.isFinite() } ?: 0f).coerceIn(0f, 1f) * 100).toInt()
        "downloading $pct%"
    }
    "done" -> "done ✓ ${job.filename ?: ""}"
    "failed" -> "failed"
    "cancelled" -> "cancelled"
    else -> job.status
}

private fun formatDuration(sec: Long): String {
    if (sec <= 0) return "--:--"
    return "%d:%02d".format(sec / 60, sec % 60)
}
