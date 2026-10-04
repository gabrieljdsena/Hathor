package com.musicplayer.android.ui.shell

import androidx.compose.animation.core.tween
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
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
import com.musicplayer.android.ui.theme.HathorColors

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
    Download("Download", Icons.Filled.Download),
    Podcasts("Podcasts", Icons.Filled.Mic),
    Playlists("Playlists", Icons.Filled.List),
    History("History", Icons.Filled.History),
    Settings("Settings", Icons.Filled.Settings);

    /** Rail highlight mapping (desktop: all_songs/daily_mix -> home). */
    fun railActive(): Dest = when (this) {
        AllSongs, DailyMix -> Library
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
    nowPlayingTitle: String?,
    nowPlayingArtist: String?,
    nowPlayingCover: android.graphics.Bitmap?,
    selected: Dest,
    onSelect: (Dest) -> Unit,
    onBarClick: () -> Unit,
    wallpaper: android.graphics.Bitmap?,
    content: @Composable () -> Unit
) {
    var expanded by remember { mutableStateOf(true) }

    Column(modifier = Modifier.fillMaxSize().background(HathorColors.Background)) {
        Row(modifier = Modifier.weight(1f).fillMaxWidth()) {
            // ---- Sidebar (desktop aside#sidebar, collapsible) ----
            Column(
                modifier = Modifier
                    .width(if (expanded) 88.dp else 60.dp)
                    .fillMaxHeight()
                    .background(Color(0x66000000))
                    .verticalScroll(rememberScrollState())
                    .padding(vertical = 12.dp, horizontal = if (expanded) 8.dp else 6.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                // Brand tile: the Ht app mark on zinc-950, like the launcher icon.
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
                if (expanded) {
                    Text("Hathor", style = MaterialTheme.typography.bodySmall, color = HathorColors.TextBody)
                }
                IconButton(onClick = { expanded = !expanded }) {
                    Icon(
                        if (expanded) Icons.Filled.ChevronLeft else Icons.Filled.ChevronRight,
                        contentDescription = if (expanded) "Collapse sidebar" else "Expand sidebar",
                        tint = HathorColors.TextHint
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
                    val labelColor by androidx.compose.animation.animateColorAsState(
                        targetValue = if (active) Color.White else HathorColors.TextHint,
                        animationSpec = tween(durationMillis = 300),
                        label = "rail-label"
                    )
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(12.dp))
                            .background(if (active) HathorColors.AccentFill else Color.Transparent)
                            .clickable { onSelect(dest) }
                            .padding(vertical = 10.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Icon(
                            dest.icon,
                            contentDescription = dest.label,
                            tint = iconTint
                        )
                        if (expanded) {
                            Text(
                                dest.label,
                                color = labelColor,
                                style = MaterialTheme.typography.bodySmall
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
                Box(modifier = Modifier.fillMaxSize()) { content() }
            }
        }
        // ---- Bottom player bar (desktop fixed bottom bar) ----
        PlayerBar(
            player = player,
            title = nowPlayingTitle,
            artist = nowPlayingArtist,
            cover = nowPlayingCover,
            onClick = onBarClick
        )
    }
}

@Composable
private fun PlayerBar(player: PlayerManager, title: String?, artist: String?, cover: android.graphics.Bitmap?, onClick: () -> Unit) {
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
                // Playing: visualizer bars; paused with art: the cover;
                // otherwise the note glyph.
                if (playing) {
                    com.musicplayer.android.ui.VisualizerBars()
                } else if (cover != null) {
                    androidx.compose.foundation.Image(
                        bitmap = cover.asImageBitmap(),
                        contentDescription = "Cover",
                        modifier = Modifier.size(44.dp)
                    )
                } else {
                    Icon(Icons.Filled.MusicNote, contentDescription = null, tint = HathorColors.AccentBright)
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
        }
    }
}
