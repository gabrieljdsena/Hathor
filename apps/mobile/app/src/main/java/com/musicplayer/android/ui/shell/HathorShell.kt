package com.musicplayer.android.ui.shell

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandHorizontally
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkHorizontally
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Album
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Explore
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.Lyrics
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.playback.PreviewPlayer
import com.musicplayer.android.ui.SyncProgress
import com.musicplayer.android.ui.HathorArtwork
import com.musicplayer.android.ui.SyncProgressBar
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.ModernSlider
import com.musicplayer.android.ui.theme.hathorPressable

/**
 * Destinations mirror the desktop sidebar (Home, Download, Playlists,
 * History, Settings) plus the Home-dashboard children (All Songs, Daily
 * Mix). The rail only shows the 5 sidebar entries; All Songs / Daily Mix
 * highlight Home (desktop navigateTo sidebar sync) and are reached via the
 * dashboard cards + back buttons.
 */
enum class Dest(val label: String, val icon: ImageVector, val inRail: Boolean = true) {
    Library("Home", Icons.Filled.Home),
    AllSongs("All Songs", Icons.Filled.MusicNote, inRail = false),
    DailyMix("Daily Mix", Icons.Filled.Refresh, inRail = false),
    Artists("Artists", Icons.Filled.Person, inRail = false),
    Albums("Albums", Icons.Filled.Album, inRail = false),
    Discover("Discover", Icons.Filled.Explore),
    Download("Download", Icons.Filled.Download),
    Podcasts("Podcasts", Icons.Filled.Mic),
    Playlists("Playlists", Icons.Filled.List),
    History("History", Icons.Filled.History),
    AiDj("AI DJ", Icons.Filled.AutoAwesome),
    Settings("Settings", Icons.Filled.Settings);

    /** Rail highlight mapping (desktop: all_songs/daily_mix -> home). */
    fun railActive(): Dest = when (this) {
        AllSongs, DailyMix, Artists, Albums -> Library
        else -> this
    }
}

/**
 * Desktop shell port: collapsible glass sidebar (brand tile + nav with orange
 * active indicator, like aside#sidebar + sidebar-toggle) over ambient orange
 * glows, content, and the bottom player bar (desktop fixed bottom bar).
 */
@Composable
fun HathorShell(
    player: PlayerManager,
    preview: PreviewPlayer,
    nowPlayingTitle: String?,
    nowPlayingArtist: String?,
    nowPlayingCover: android.graphics.Bitmap?,
    selected: Dest,
    onSelect: (Dest) -> Unit,
    onBarClick: () -> Unit,
    onOpenLyrics: () -> Unit,
    wallpaper: android.graphics.Bitmap?,
    syncProgress: SyncProgress? = null,
    content: @Composable () -> Unit
) {
    // The old icon-only "minimized" rail is now the open state. Closing the
    // sidebar slides the rail away and reveals a small open arrow instead.
    var sidebarVisible by remember { mutableStateOf(true) }
    // Keep the content header in the same place while the rail animates.
    val contentStart by animateDpAsState(
        targetValue = if (sidebarVisible) 0.dp else 40.dp,
        animationSpec = tween(durationMillis = 220),
        label = "content-start"
    )

    Column(modifier = Modifier.fillMaxSize().background(HathorColors.Background)) {
        Row(modifier = Modifier.weight(1f).fillMaxWidth()) {
            AnimatedVisibility(
                visible = sidebarVisible,
                enter = expandHorizontally(animationSpec = tween(220)) +
                    fadeIn(animationSpec = tween(220)),
                exit = shrinkHorizontally(animationSpec = tween(180)) +
                    fadeOut(animationSpec = tween(140))
            ) {
                // ---- Icon-only rail (the previous minimized layout) ----
                Column(
                    modifier = Modifier
                        .width(60.dp)
                        .fillMaxHeight()
                        .background(Color(0x66000000))
                        .verticalScroll(rememberScrollState())
                        .padding(vertical = 12.dp, horizontal = 6.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    // Brand tile: the HT app mark on zinc-950, like the launcher icon.
                    Box(
                        modifier = Modifier
                            .size(44.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(Color(0xFF09090B)),
                        contentAlignment = Alignment.Center
                    ) {
                        androidx.compose.foundation.Image(
                            painter = androidx.compose.ui.res.painterResource(id = com.musicplayer.android.R.drawable.app_logo),
                            contentDescription = "Hathor",
                            modifier = Modifier.size(28.dp)
                        )
                    }
                    IconButton(onClick = { sidebarVisible = false }) {
                        Icon(
                            Icons.Filled.ChevronLeft,
                            contentDescription = "Hide sidebar",
                            tint = HathorColors.TextHint,
                            modifier = Modifier.size(22.dp)
                        )
                    }
                    val selectedRail = selected.railActive()
                    Dest.entries.filter { it.inRail }.forEach { dest ->
                        val active = dest == selectedRail
                        // Desktop group-[.active-nav] transitions, animated.
                        val iconTint by androidx.compose.animation.animateColorAsState(
                            targetValue = if (active) HathorColors.AccentBright else HathorColors.TextHint,
                            animationSpec = tween(durationMillis = 300),
                            label = "rail-icon"
                        )
                        val itemBackground by androidx.compose.animation.animateColorAsState(
                            targetValue = if (active) HathorColors.AccentFill else Color.Transparent,
                            animationSpec = tween(durationMillis = 300),
                            label = "rail-background"
                        )
                        val indicatorColor by androidx.compose.animation.animateColorAsState(
                            targetValue = if (active) HathorColors.AccentBright else Color.Transparent,
                            animationSpec = tween(durationMillis = 300),
                            label = "rail-indicator"
                        )
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .background(itemBackground)
                                .hathorPressable { onSelect(dest) }
                                .padding(vertical = 10.dp),
                            horizontalAlignment = Alignment.CenterHorizontally
                        ) {
                            Icon(
                                dest.icon,
                                contentDescription = dest.label,
                                tint = iconTint
                            )
                            Box(
                                modifier = Modifier
                                    .padding(top = 4.dp)
                                    .size(4.dp)
                                    .clip(CircleShape)
                                    .background(indicatorColor)
                            )
                        }
                    }
                }
            }
            // ---- Content over ambient glows (desktop body glow divs) ----
            // Optional wallpaper sits under everything with the desktop's
            // 40% black scrim (apply_background overlay), cover-cropped.
            Box(modifier = Modifier.weight(1f).fillMaxHeight().background(HathorColors.Background)) {
                if (wallpaper != null) {
                    androidx.compose.foundation.Image(
                        bitmap = wallpaper.asImageBitmap(),
                        contentDescription = null,
                        modifier = Modifier.fillMaxSize(),
                        contentScale = androidx.compose.ui.layout.ContentScale.Crop
                    )
                    Box(
                        modifier = Modifier.fillMaxSize()
                            .background(Color(0x6609090B)) // rgba(9,9,11,0.4)
                    )
                }
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(
                            Brush.radialGradient(
                                colors = listOf(HathorColors.GlowTop, Color.Transparent),
                                radius = 700f
                            )
                        )
                )
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(
                            Brush.radialGradient(
                                colors = listOf(HathorColors.GlowBottom, Color.Transparent),
                                radius = 700f
                            )
                        )
                )
                Box(modifier = Modifier.fillMaxSize()) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            // Reserve the open-arrow's leading slot so the
                            // screen header sits on the same row, never under it.
                            .padding(start = contentStart)
                    ) {
                        if (syncProgress?.running == true) {
                            SyncProgressBar(syncProgress)
                        }
                        Box(modifier = Modifier.weight(1f)) { content() }
                    }
                    androidx.compose.animation.AnimatedVisibility(
                        visible = !sidebarVisible,
                        enter = fadeIn(animationSpec = tween(200, delayMillis = 120)),
                        exit = fadeOut(animationSpec = tween(80)),
                        modifier = Modifier.align(Alignment.TopStart)
                    ) {
                        Box(modifier = Modifier.padding(start = 4.dp, top = 16.dp)) {
                            IconButton(
                                onClick = { sidebarVisible = true },
                                modifier = Modifier.size(36.dp)
                            ) {
                                Icon(
                                    Icons.Filled.ChevronRight,
                                    contentDescription = "Show sidebar",
                                    tint = HathorColors.TextBody,
                                    modifier = Modifier.size(22.dp)
                                )
                            }
                        }
                    }
                }
            }
        }
        if (selected != Dest.Discover) {
            PreviewDock(preview)
        }
        // ---- Bottom player bar (desktop fixed bottom bar) ----
        PlayerBar(
            player = player,
            title = nowPlayingTitle,
            artist = nowPlayingArtist,
            cover = nowPlayingCover,
            onClick = onBarClick,
            onOpenLyrics = onOpenLyrics
        )
    }
}

@Composable
private fun PreviewDock(preview: PreviewPlayer) {
    val state by preview.state.collectAsState()
    val info by preview.info.collectAsState()
    val position by preview.positionMs.collectAsState()
    val duration by preview.durationMs.collectAsState()
    val playing = state is PreviewPlayer.State.Playing
    val loading = state is PreviewPlayer.State.Loading
    val current = info ?: return
    if (!playing && !loading) return
    val fraction = if (duration > 0) {
        (position.toFloat() / duration.toFloat()).coerceIn(0f, 1f)
    } else {
        0f
    }
    Column(
        modifier = Modifier.fillMaxWidth().background(Color(0x66000000)).padding(horizontal = 12.dp, vertical = 4.dp)
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Box(modifier = Modifier.size(8.dp).clip(RoundedCornerShape(50)).background(HathorColors.AccentBright))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    current.title.ifBlank { "Discover preview" },
                    style = MaterialTheme.typography.bodyMedium,
                    color = HathorColors.TextPrimary,
                    maxLines = 1
                )
                Text(
                    listOfNotNull(
                        current.artist.takeIf { it.isNotBlank() },
                        "Discover preview"
                    ).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1
                )
            }
            if (loading) {
                CircularProgressIndicator(
                    modifier = Modifier.size(16.dp),
                    strokeWidth = 2.dp,
                    color = HathorColors.AccentBright
                )
            }
            IconButton(onClick = { preview.stop() }) {
                Icon(Icons.Filled.Stop, contentDescription = "Stop preview", tint = HathorColors.TextBody)
            }
        }
        if (playing && duration > 0) {
            ModernSlider(
                value = fraction,
                onValueChange = { preview.seekToFraction(it) },
                modifier = Modifier.fillMaxWidth()
            )
        }
    }
}

@Composable
private fun PlayerBar(
    player: PlayerManager,
    title: String?,
    artist: String?,
    cover: android.graphics.Bitmap?,
    onClick: () -> Unit,
    onOpenLyrics: () -> Unit
) {
    val playing by player.playing.collectAsState()
    val position by player.positionMs.collectAsState()
    val duration by player.durationMs.collectAsState()
    val current by player.current.collectAsState()

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(Color(0x66000000))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 8.dp)
    ) {
        com.musicplayer.android.ui.theme.ModernSlider(
            value = if (duration > 0) position.toFloat() / duration.toFloat() else 0f,
            onValueChange = { if (duration > 0) player.seekTo((it * duration).toInt()) },
            enabled = current != null && duration > 0,
            modifier = Modifier.fillMaxWidth()
        )
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(HathorColors.AccentFill),
                contentAlignment = Alignment.Center
            ) {
                // Playing: visualizer bars; otherwise crossfading cover art
                // with the shared accent placeholder.
                if (playing) {
                    com.musicplayer.android.ui.VisualizerBars()
                } else {
                    HathorArtwork(
                        bitmap = cover,
                        contentDescription = "Cover",
                        modifier = Modifier.size(44.dp),
                        cornerRadius = 8.dp
                    )
                }
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    title ?: if (current != null) current!!.nameWithoutExtension else "Nothing playing",
                    style = MaterialTheme.typography.bodyMedium,
                    color = HathorColors.TextPrimary,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
                Text(
                    artist ?: "",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                )
            }
            IconButton(onClick = { player.prev() }, enabled = current != null) {
                Icon(Icons.Filled.SkipPrevious, contentDescription = "Previous", tint = HathorColors.TextPrimary)
            }
            IconButton(onClick = { player.toggle() }) {
                Icon(
                    if (playing) Icons.Filled.Pause else Icons.Filled.PlayArrow,
                    contentDescription = if (playing) "Pause" else "Play",
                    tint = HathorColors.TextPrimary
                )
            }
            IconButton(onClick = { player.next() }, enabled = current != null) {
                Icon(Icons.Filled.SkipNext, contentDescription = "Next", tint = HathorColors.TextPrimary)
            }
            IconButton(onClick = onOpenLyrics, enabled = current != null) {
                Icon(Icons.Filled.Lyrics, contentDescription = "Lyrics", tint = HathorColors.TextPrimary)
            }
        }
    }
}
