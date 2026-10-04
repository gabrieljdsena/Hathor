package com.musicplayer.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Refresh
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
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import com.musicplayer.android.data.DailyMixRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.playback.PlaybackSource
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HeaderIconTile
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Locale

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
    onOpenAllSongs: () -> Unit,
    onOpenDailyMix: () -> Unit,
    onOpenHistory: () -> Unit,
    onPlay: (SongMeta, List<SongMeta>, PlaybackSource) -> Unit,
    onPlayNext: (SongMeta) -> Unit,
    onEnqueue: (SongMeta) -> Unit,
    nowPlayingFile: String?,
    isPlaying: Boolean
) {
    val songs by library.library.collectAsState()
    var mixSize by remember { mutableStateOf<Int?>(null) }
    var mixDate by remember { mutableStateOf<String?>(null) }
    var recent by remember { mutableStateOf<List<SongMeta>>(emptyList()) }
    var downloaded by remember { mutableStateOf<List<SongMeta>>(emptyList()) }
    var loading by remember { mutableStateOf(true) }
    // Strip context menu (long-press opens the same menu as song rows).
    var menuSong by remember { mutableStateOf<Triple<SongMeta, List<SongMeta>, PlaybackSource>?>(null) }

    suspend fun reload() {
        loading = true
        try {
            withContext(Dispatchers.IO) { library.refreshLibrary() }
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
        } catch (_: Exception) {
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

    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp)
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

        // Cards
        HomeCard(
            title = "All Songs",
            subtitle = if (songs.isEmpty() && loading) "Loading library…"
            else "${songs.size} song${if (songs.size == 1) "" else "s"} in your library",
            hint = "Browse, search and sort your full library.",
            icon = { Icon(Icons.Filled.MusicNote, contentDescription = null, tint = androidx.compose.ui.graphics.Color.White) },
            onClick = onOpenAllSongs
        )
        HomeCard(
            title = "Daily Mix",
            subtitle = if (mixSize == null) "Generating your mix…"
            else "$mixSize songs · $mixDateShort",
            hint = "Fresh every day.",
            badge = "Today",
            icon = { Icon(Icons.Filled.Refresh, contentDescription = null, tint = androidx.compose.ui.graphics.Color.White) },
            onClick = onOpenDailyMix
        )

        // Recently Played strip
        StripSection(
            title = "Recently Played",
            songs = recent,
            emptyText = "Nothing played yet — play something and it will show up here.",
            library = library,
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
            nowPlayingFile = nowPlayingFile,
            isPlaying = isPlaying,
            onSeeAll = onOpenHistory,
            onPlay = { song -> onPlay(song, downloaded, PlaybackSource("recently_downloaded", null)) },
            onMenu = { song ->
                menuSong = Triple(song, downloaded, PlaybackSource("recently_downloaded", null))
            }
        )
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
            onDismiss = { menuSong = null }
        )
    }
}

@Composable
private fun HomeCard(
    title: String,
    subtitle: String,
    hint: String,
    badge: String? = null,
    icon: @Composable () -> Unit,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth().hathorGlass().clickable(onClick = onClick).padding(20.dp),
        horizontalArrangement = Arrangement.spacedBy(16.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier.size(56.dp)
                .clip(androidx.compose.foundation.shape.RoundedCornerShape(16.dp))
                .background(HathorColors.BrandGradient),
            contentAlignment = Alignment.Center
        ) { icon() }
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                badge?.let {
                    Text(
                        it.uppercase(),
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.AccentBright
                    )
                }
            }
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
            Text(hint, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        }
    }
}

@Composable
private fun StripSection(
    title: String,
    songs: List<SongMeta>,
    emptyText: String,
    library: MetadataRepository,
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
        if (songs.isEmpty()) {
            Text(emptyText, style = MaterialTheme.typography.bodySmall, color = HathorColors.TextHint)
        } else {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
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
