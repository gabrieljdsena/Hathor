package com.musicplayer.android.ui

import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.hathorGlass

/**
 * Shared list infrastructure (desktop index.html global helpers:
 * generateSongRowHtml + set_active_song_ui + lazyCoverObserver, loaded once
 * by the app shell). Every list view uses these — no view depends on another
 * having loaded.
 *
 * Active song: ring + orange title + animated visualizer badge (desktop
 * .song-row active classes + .list-visualizer), including when returning to
 * a view while something plays (callers pass nowPlayingFile/isPlaying).
 */

/** Lazy cover thumbnail (desktop lazyCoverObserver equivalent). */
@Composable
fun SongCover(
    repo: MetadataRepository,
    fileName: String,
    size: Dp = 48.dp,
    isPodcast: Boolean = false
) {
    var bitmap by remember(fileName, isPodcast) { mutableStateOf<android.graphics.Bitmap?>(null) }
    val density = LocalDensity.current
    LaunchedEffect(fileName, isPodcast) {
        val px = with(density) { size.toPx() }.toInt() * 2
        bitmap = try {
            repo.coverForSong(
                SongMeta(fileName, fileName, "", "", "", 0L, null, false, isPodcast = isPodcast),
                px
            )
        } catch (_: Exception) {
            null
        }
    }
    Box(
        modifier = Modifier.size(size).clip(RoundedCornerShape(8.dp)),
        contentAlignment = Alignment.Center
    ) {
        val bmp = bitmap
        if (bmp != null) {
            androidx.compose.foundation.Image(
                bitmap = bmp.asImageBitmap(),
                contentDescription = "Cover",
                modifier = Modifier.size(size)
            )
        } else {
            Icon(
                Icons.Filled.MusicNote,
                contentDescription = null,
                tint = HathorColors.AccentBright,
                modifier = Modifier.size(size * 0.6f)
            )
        }
    }
}

/** Full song row (All Songs / Daily Mix / playlist detail / history). */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun SongRow(
    song: SongMeta,
    repo: MetadataRepository,
    isActive: Boolean,
    isPlaying: Boolean,
    onClick: () -> Unit,
    onMenu: (() -> Unit)? = null,
    dateText: String? = null
) {
    val titleColor = if (isActive) HathorColors.AccentBright else HathorColors.TextPrimary
    Row(
        modifier = Modifier.fillMaxWidth()
            .hathorGlass()
            .then(
                if (isActive) Modifier.border(
                    1.dp, HathorColors.Accent.copy(alpha = 0.3f),
                    RoundedCornerShape(16.dp)
                ) else Modifier
            )
            // Right-click equivalent on touch: long-press opens the same
            // context menu as the ⋮ button (text fields keep native menus).
            .combinedClickable(onClick = onClick, onLongClick = onMenu)
            .padding(8.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (isActive && isPlaying) {
            VisualizerBars(modifier = Modifier.size(48.dp, 20.dp))
        } else {
            SongCover(repo = repo, fileName = song.file, size = 48.dp, isPodcast = song.isPodcast)
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(
                song.title.ifBlank { song.file },
                style = MaterialTheme.typography.bodyMedium,
                color = titleColor,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                listOfNotNull(
                    song.artist.takeIf { it.isNotBlank() && it != "Unknown" },
                    song.album.takeIf { it.isNotBlank() && it != "Unknown" }
                ).joinToString(" · ").ifBlank { formatRowDuration(song.durationSec) },
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        Column(horizontalAlignment = Alignment.End) {
            Text(
                formatRowDuration(song.durationSec),
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
            dateText?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
        if (onMenu != null) {
            IconButton(onClick = onMenu) {
                Icon(Icons.Filled.MoreVert, contentDescription = "Menu", tint = HathorColors.TextHint)
            }
        }
    }
}

/**
 * Home strip card (desktop home-strip-card): cover/title/artist,
 * click-to-play. Active = ring + orange title + visualizer badge.
 */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun StripCard(
    song: SongMeta,
    repo: MetadataRepository,
    isActive: Boolean,
    isPlaying: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    onMenu: (() -> Unit)? = null
) {
    val titleColor = if (isActive) HathorColors.AccentBright else HathorColors.TextPrimary
    Column(
        modifier = modifier.width(144.dp)
            .hathorGlass()
            .then(
                if (isActive) Modifier.border(
                    1.dp, HathorColors.Accent.copy(alpha = 0.3f),
                    RoundedCornerShape(16.dp)
                ) else Modifier
            )
            // Long-press opens the same context menu (home strips included).
            .combinedClickable(onClick = onClick, onLongClick = onMenu)
            .padding(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Box(contentAlignment = Alignment.BottomStart) {
            // Larger strip cover with lazy load.
            var bitmap by remember(song.file, song.isPodcast) { mutableStateOf<android.graphics.Bitmap?>(null) }
            val density = LocalDensity.current
            LaunchedEffect(song.file, song.isPodcast) {
                val px = with(density) { 144.dp.toPx() }.toInt()
                bitmap = try {
                    repo.coverForSong(song, px)
                } catch (_: Exception) {
                    null
                }
            }
            val bmp = bitmap
            Box(
                modifier = Modifier.size(128.dp).clip(RoundedCornerShape(12.dp)),
                contentAlignment = Alignment.Center
            ) {
                if (bmp != null) {
                    androidx.compose.foundation.Image(
                        bitmap = bmp.asImageBitmap(),
                        contentDescription = song.title,
                        modifier = Modifier.size(128.dp)
                    )
                } else {
                    Icon(
                        Icons.Filled.MusicNote,
                        contentDescription = null,
                        tint = HathorColors.AccentBright,
                        modifier = Modifier.size(40.dp)
                    )
                }
            }
            if (isActive) {
                // Animated visualizer badge (desktop strip-playing-badge).
                Box(
                    modifier = Modifier.padding(6.dp)
                        .clip(RoundedCornerShape(8.dp))
                ) {
                    if (isPlaying) VisualizerBars() else Box(
                        modifier = Modifier.size(16.dp)
                            .clip(RoundedCornerShape(4.dp))
                            .border(1.dp, HathorColors.AccentBright, RoundedCornerShape(4.dp))
                    )
                }
            }
        }
        Text(
            song.title.ifBlank { song.file },
            style = MaterialTheme.typography.bodyMedium,
            color = titleColor,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
        Text(
            song.artist.ifBlank { "Unknown Artist" },
            style = MaterialTheme.typography.bodySmall,
            color = HathorColors.TextHint,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

private fun formatRowDuration(sec: Long): String {
    if (sec <= 0) return "--:--"
    return "%d:%02d".format(sec / 60, sec % 60)
}
