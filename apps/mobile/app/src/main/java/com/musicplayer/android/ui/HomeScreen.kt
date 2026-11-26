package com.musicplayer.android.ui

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Album
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musicplayer.android.ui.theme.hathorPressable
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.DailyMixRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.PodcastRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.data.api.PlaybackApi
import com.musicplayer.android.data.api.PlaybackSpot
import com.musicplayer.android.playback.PlaybackSource
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Locale

private data class HomeShortcut(
    val title: String,
    val subtitle: String,
    val icon: ImageVector,
    val onClick: () -> Unit
)

/**
 * Home dashboard (desktop home.html): NOT a song list. Exactly: an All
 * Songs card (live library count, opens the full library), a Daily Mix card
 * (mix size + date, opens the mix), a Recently Played horizontal strip, a
 * Recently Downloaded horizontal strip.
 */
@Composable
fun HomeScreen(
    dailyMix: DailyMixRepository,
    library: MetadataRepository,
    playlistRepo: PlaylistRepository,
    artwork: ArtworkRepository,
    podcasts: PodcastRepository,
    deviceId: String,
    onOpenAllSongs: () -> Unit,
    onOpenDailyMix: () -> Unit,
    onOpenArtists: () -> Unit,
    onOpenAlbums: () -> Unit,
    onOpenPodcasts: () -> Unit,
    onOpenHistory: () -> Unit,
    onPlayPodcast: (SongMeta) -> Unit,
    onResumeSpot: (PlaybackSpot) -> Unit,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    val songs by library.library.collectAsState()
    val podcastEpisodes by podcasts.episodes.collectAsState()
    var mixSize by remember { mutableStateOf<Int?>(null) }
    var mixDate by remember { mutableStateOf<String?>(null) }
    var recent by remember { mutableStateOf<List<SongMeta>>(emptyList()) }
    var downloaded by remember { mutableStateOf<List<SongMeta>>(emptyList()) }
    var resumeSpot by remember { mutableStateOf<PlaybackSpot?>(null) }
    var resumeTitle by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    // Strip context menu (long-press opens the same menu as song rows).
    var menuSong by remember { mutableStateOf<Triple<SongMeta, List<SongMeta>, PlaybackSource>?>(null) }
    var editSong by remember { mutableStateOf<SongMeta?>(null) }
    var playlistSong by remember { mutableStateOf<SongMeta?>(null) }

    suspend fun reload() {
        loading = true
        try {
            library.refreshLibraryIfStale()
        } catch (_: Exception) {
        }
        try {
            val mix = withContext(Dispatchers.IO) { dailyMix.getDailyMix() }
            mixSize = mix.songs.size
            mixDate = mix.date
        } catch (_: Exception) {
            mixSize = null
        }
        try {
            recent = withContext(Dispatchers.IO) { dailyMix.recentlyPlayed(15) }
            downloaded = withContext(Dispatchers.IO) { dailyMix.recentlyDownloaded(15) }
            withContext(Dispatchers.IO) { podcasts.refresh() }
        } catch (_: Exception) {
        }
        try {
            val spot = withContext(Dispatchers.IO) { PlaybackApi.latest(deviceId) }
            val local = spot?.let { s ->
                library.library.value.firstOrNull { it.file == s.file }
            }
            resumeSpot = if (local != null) spot else null
            resumeTitle = local?.title
        } catch (_: Exception) {
            resumeSpot = null
            resumeTitle = null
        }
        loading = false
    }

    LaunchedEffect(Unit) { reload() }

    val dateSubtitle = remember {
        try {
            SimpleDateFormat("EEEE, MMMM d", Locale.getDefault()).format(java.util.Date())
        } catch (_: Exception) {
            "Pick what to listen to"
        }
    }
    val mixDateShort = remember(mixDate) {
        val d = mixDate ?: return@remember ""
        try {
            val parsed = SimpleDateFormat("yyyy-MM-dd", Locale.US).parse(d) ?: return@remember d
            SimpleDateFormat("MMM d", Locale.getDefault()).format(parsed)
        } catch (_: Exception) {
            d
        }
    }

    val configuration = LocalConfiguration.current
    val wide = configuration.screenWidthDp >= 840
    val compact = configuration.screenHeightDp < 500
    val artistCount = songs.map { it.artist }.distinct().size
    val albumCount = songs.map { it.album }.distinct().size
    val shortcuts = listOf(
        HomeShortcut(
            title = "All Songs",
            subtitle = if (songs.isEmpty() && loading) "Loading…"
            else "${songs.size} song${if (songs.size == 1) "" else "s"}",
            icon = Icons.Filled.MusicNote,
            onClick = onOpenAllSongs
        ),
        HomeShortcut(
            title = "Artists",
            subtitle = "$artistCount artist${if (artistCount == 1) "" else "s"}",
            icon = Icons.Filled.Person,
            onClick = onOpenArtists
        ),
        HomeShortcut(
            title = "Albums",
            subtitle = "$albumCount album${if (albumCount == 1) "" else "s"}",
            icon = Icons.Filled.Album,
            onClick = onOpenAlbums
        ),
        HomeShortcut(
            title = "Podcasts",
            subtitle = if (podcastEpisodes.isEmpty()) "No episodes"
            else "${podcastEpisodes.size} episode${if (podcastEpisodes.size == 1) "" else "s"}",
            icon = Icons.Filled.Mic,
            onClick = onOpenPodcasts
        )
    )

    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .widthIn(max = 1100.dp)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(if (compact) 10.dp else 20.dp)
        ) {
            // Header
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                HeaderIconTile {
                    Icon(Icons.Filled.Home, contentDescription = null, tint = HathorColors.AccentBright)
                }
                Column {
                    Text("Home", style = MaterialTheme.typography.titleLarge)
                    Text(dateSubtitle, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
                }
            }

            // Hero: the mix is the day's primary action.
            DailyMixHero(
                mixSize = mixSize,
                mixDate = mixDateShort,
                loading = loading,
                compact = compact,
                onClick = onOpenDailyMix
            )

            // Compact library shortcuts: one row on wide screens, 2x2 otherwise.
            if (wide) {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    shortcuts.forEach { shortcut ->
                        ShortcutCard(
                            title = shortcut.title,
                            subtitle = shortcut.subtitle,
                            icon = shortcut.icon,
                            onClick = shortcut.onClick,
                            compact = compact,
                            modifier = Modifier.weight(1f)
                        )
                    }
                }
            } else {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    shortcuts.chunked(2).forEach { row ->
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            row.forEach { shortcut ->
                                ShortcutCard(
                                    title = shortcut.title,
                                    subtitle = shortcut.subtitle,
                                    icon = shortcut.icon,
                                    onClick = shortcut.onClick,
                                    compact = compact,
                                    modifier = Modifier.weight(1f)
                                )
                            }
                        }
                    }
                }
            }
            resumeSpot?.let { spot ->
                val pos = "%d:%02d".format(spot.positionSec.toInt() / 60, spot.positionSec.toInt() % 60)
                ResumeCard(
                    title = "Continue on ${spot.device ?: "another device"}",
                    subtitle = "${resumeTitle ?: spot.file} · $pos",
                    onClick = { onResumeSpot(spot) }
                )
            }

            // Recently Played strip
            StripSection(
                title = "Recently Played",
                songs = recent,
                emptyText = "Nothing played yet — play something and it will show up here.",
                library = library,
                loading = loading,
                nowPlayingFile = nowPlayingFile,
                isPlaying = isPlaying,
                onSeeAll = onOpenHistory,
                onPlay = { song -> onPlay(song, recent, PlaybackSource("recently_played", null)) },
                onMenu = { song ->
                    menuSong = Triple(song, recent, PlaybackSource("recently_played", null))
                }
            )

            // Recently Downloaded strip
            StripSection(
                title = "Recently Downloaded",
                songs = downloaded,
                emptyText = "Nothing downloaded yet — grab some music and it will show up here.",
                library = library,
                loading = loading,
                nowPlayingFile = nowPlayingFile,
                isPlaying = isPlaying,
                onSeeAll = onOpenHistory,
                onPlay = { song -> onPlay(song, downloaded, PlaybackSource("recently_downloaded", null)) },
                onMenu = { song ->
                    menuSong = Triple(song, downloaded, PlaybackSource("recently_downloaded", null))
                }
            )
        }
    }

    menuSong?.let { (song, visible, source) ->
        SongMenuDialog(
            song = song,
            visible = visible,
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
private fun DailyMixHero(
    mixSize: Int?,
    mixDate: String,
    loading: Boolean,
    compact: Boolean,
    onClick: () -> Unit
) {
    val subtitle = when {
        loading -> "Generating your mix…"
        mixSize == null -> "Mix unavailable"
        mixSize == 1 -> "1 song · $mixDate"
        else -> "$mixSize songs · $mixDate"
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(20.dp))
            .background(
                Brush.linearGradient(
                    listOf(
                        HathorColors.Accent.copy(alpha = 0.34f),
                        HathorColors.AccentDeep.copy(alpha = 0.12f),
                        HathorColors.SurfaceGlass
                    )
                )
            )
            .border(
                1.dp,
                HathorColors.Accent.copy(alpha = 0.30f),
                RoundedCornerShape(20.dp)
            )
            .hathorPressable(onClick)
            .padding(if (compact) 12.dp else 18.dp),
        horizontalArrangement = Arrangement.spacedBy(if (compact) 12.dp else 16.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(if (compact) 44.dp else 56.dp)
                .clip(RoundedCornerShape(if (compact) 14.dp else 18.dp))
                .background(HathorColors.BrandGradient),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                Icons.Filled.Refresh,
                contentDescription = null,
                tint = Color.White,
                modifier = Modifier.size(if (compact) 22.dp else 28.dp)
            )
        }
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("Daily Mix", style = MaterialTheme.typography.titleMedium)
                if (!loading && mixSize != null) {
                    Text(
                        "TODAY",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.AccentBright
                    )
                }
            }
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextBody)
        }
        Icon(
            Icons.Filled.ChevronRight,
            contentDescription = "Open Daily Mix",
            tint = HathorColors.AccentBright
        )
    }
}

@Composable
private fun ShortcutCard(
    title: String,
    subtitle: String,
    icon: ImageVector,
    onClick: () -> Unit,
    compact: Boolean = false,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .height(if (compact) 86.dp else 124.dp)
            .hathorGlass()
            .hathorPressable(onClick)
            .padding(if (compact) 10.dp else 14.dp),
        verticalArrangement = Arrangement.spacedBy(if (compact) 6.dp else 10.dp)
    ) {
        ShortcutIcon(
            icon,
            size = if (compact) 30.dp else 40.dp,
            iconSize = if (compact) 17.dp else 22.dp,
            radius = if (compact) 10.dp else 12.dp
        )
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                title,
                style = MaterialTheme.typography.titleSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}

@Composable
private fun ResumeCard(
    title: String,
    subtitle: String,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .hathorGlass()
            .hathorPressable(onClick)
            .padding(14.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        ShortcutIcon(Icons.Filled.PlayArrow)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                title,
                style = MaterialTheme.typography.titleSmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        Icon(
            Icons.Filled.PlayArrow,
            contentDescription = "Resume",
            tint = HathorColors.AccentBright
        )
    }
}

@Composable
private fun ShortcutIcon(
    icon: ImageVector,
    size: Dp = 40.dp,
    iconSize: Dp = 22.dp,
    radius: Dp = 12.dp
) {
    Box(
        modifier = Modifier
            .size(size)
            .clip(RoundedCornerShape(radius))
            .background(HathorColors.AccentFill)
            .border(1.dp, HathorColors.Accent.copy(alpha = 0.2f), RoundedCornerShape(radius)),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = HathorColors.AccentBright,
            modifier = Modifier.size(iconSize)
        )
    }
}

@Composable
private fun StripSection(
    title: String,
    songs: List<SongMeta>,
    emptyText: String,
    library: MetadataRepository,
    loading: Boolean = false,
    nowPlayingFile: String?,
    isPlaying: Boolean,
    onSeeAll: () -> Unit,
    onPlay: (SongMeta) -> Unit,
    onMenu: (SongMeta) -> Unit = {}
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            TextButton(onClick = onSeeAll) {
                Text(
                    "See all",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            }
        }
        when {
            loading -> LazyRow(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                items(3) { StripSkeleton() }
            }
            songs.isEmpty() -> Text(
                emptyText,
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint
            )
            else -> LazyRow(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                items(songs, key = { it.file }) { song ->
                    StripCard(
                        song = song,
                        repo = library,
                        isActive = song.file == nowPlayingFile,
                        isPlaying = isPlaying && song.file == nowPlayingFile,
                        onClick = { onPlay(song) },
                        onMenu = { onMenu(song) }
                    )
                }
            }
        }
    }
}

@Composable
private fun StripSkeleton() {
    val transition = rememberInfiniteTransition(label = "strip-skeleton")
    val pulse by transition.animateFloat(
        initialValue = 0.10f,
        targetValue = 0.24f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 900),
            repeatMode = RepeatMode.Reverse
        ),
        label = "strip-skeleton-alpha"
    )
    Column(
        modifier = Modifier.width(156.dp).hathorGlass().padding(8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Box(
            modifier = Modifier
                .size(140.dp)
                .clip(RoundedCornerShape(12.dp))
                .background(HathorColors.TextHint.copy(alpha = pulse))
        )
        Box(
            modifier = Modifier
                .height(14.dp)
                .fillMaxWidth(0.85f)
                .clip(RoundedCornerShape(4.dp))
                .background(HathorColors.TextHint.copy(alpha = pulse))
        )
        Box(
            modifier = Modifier
                .height(10.dp)
                .fillMaxWidth(0.55f)
                .clip(RoundedCornerShape(4.dp))
                .background(HathorColors.TextHint.copy(alpha = pulse))
        )
    }
}
