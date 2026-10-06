package com.musicplayer.android

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import com.musicplayer.android.data.remote.PullWorker
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
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
import androidx.compose.ui.Modifier
import com.musicplayer.android.data.ArtworkRepository
import com.musicplayer.android.data.HistoryRepository
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.LyricsRepository
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.PlaylistRepository
import com.musicplayer.android.data.QueueRepository
import com.musicplayer.android.data.SettingsRepository
import com.musicplayer.android.data.SyncRepository
import com.musicplayer.android.engine.DownloadEngine
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.playback.PreviewPlayer
import com.musicplayer.android.ui.DownloadScreen
import com.musicplayer.android.ui.HistoryScreen
import com.musicplayer.android.ui.LibraryScreen
import com.musicplayer.android.ui.NowPlayingSheet
import com.musicplayer.android.ui.PlaylistsScreen
import com.musicplayer.android.ui.SettingsScreen
import com.musicplayer.android.ui.shell.Dest
import com.musicplayer.android.ui.shell.HathorShell
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorMotion
import com.musicplayer.android.ui.theme.HathorTheme
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

class MainActivity : ComponentActivity() {

    private lateinit var engine: DownloadEngine
    private lateinit var queueRepo: QueueRepository
    private lateinit var syncRepo: SyncRepository
    private lateinit var metadataRepo: MetadataRepository
    private lateinit var playlistRepo: PlaylistRepository
    private lateinit var historyRepo: HistoryRepository
    private lateinit var lyricsRepo: LyricsRepository
    private lateinit var artworkRepo: ArtworkRepository
    private lateinit var settingsRepo: SettingsRepository
    private lateinit var dailyMixRepo: com.musicplayer.android.data.DailyMixRepository
    private lateinit var podcastRepo: com.musicplayer.android.data.PodcastRepository
    private lateinit var player: PlayerManager
    private lateinit var previewPlayer: PreviewPlayer

    companion object {
        const val DEFAULT_TEST_URL = "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Shared process-lifetime instances (also used by workers/service).
        engine = PlayerApp.instance.downloadEngine
        player = PlayerApp.instance.player
        previewPlayer = PlayerApp.instance.previewPlayer
        queueRepo = PlayerApp.instance.queueRepo
        syncRepo = PlayerApp.instance.syncRepo
        metadataRepo = MetadataRepository(this, engine)
        playlistRepo = PlaylistRepository(this, metadataRepo)
        historyRepo = HistoryRepository(this, metadataRepo)
        lyricsRepo = LyricsRepository(this)
        artworkRepo = ArtworkRepository()
        settingsRepo = SettingsRepository(this, engine, metadataRepo)
        dailyMixRepo = com.musicplayer.android.data.DailyMixRepository(this, engine, metadataRepo)
        podcastRepo = com.musicplayer.android.data.PodcastRepository(this, engine, metadataRepo)
        settingsRepo.onCrossfadeChanged = { enabled, seconds ->
            player.setCrossfade(enabled, seconds)
        }
        // Chapter auto-skip (desktop set_chapter_skip live-apply parity):
        // the player caches marks per track, so it needs the pref + a
        // resolver + a podcast classifier, all wired here (it owns both).
        player.chapterProvider = { fileName -> podcastRepo.chaptersFor(fileName) }
        player.isPodcastFile = { f ->
            try {
                f.canonicalPath.startsWith(PlayerApp.instance.podcastsDir.canonicalPath)
            } catch (_: Exception) {
                false
            }
        }
        settingsRepo.onChapterSkipChanged = { player.setChapterSkip(it) }
        // Restore persisted prefs at startup (folders, volume, crossfade):
        // Settings might never be opened, but the player needs its prefs.
        CoroutineScope(Dispatchers.IO).launch {
            try {
                settingsRepo.load()
                withContext(Dispatchers.Main) {
                    player.setChapterSkip(settingsRepo.chapterSkip.value)
                }
            } catch (e: Exception) {
                android.util.Log.w("MainActivity", "settings load failed: ${e.message}")
            }
        }
        settingsRepo.onLimitChanged = { queueRepo.setLimit(it) }
        requestNotificationPermission()
        // Manual-only sync (spec §5): no background thread/timer anywhere.
        // Push = Settings Push button; pull = first-run prompt + Pull button.
        // Desktop _persist_queue + current_song: persist order + track.
        // Flags resolved per file so mixed queues restore intact.
        player.onQueueChanged = { files ->
            CoroutineScope(Dispatchers.IO).launch {
                try {
                    val podRoot = try {
                        PlayerApp.instance.podcastsDir.canonicalPath
                    } catch (_: Exception) {
                        null
                    }
                    settingsRepo.saveQueueFiles(
                        files.map { f ->
                            val isPod = podRoot != null && try {
                                f.canonicalPath.startsWith(podRoot)
                            } catch (_: Exception) {
                                false
                            }
                            SettingsRepository.QueuedFile(f.name, isPod)
                        }
                    )
                } catch (e: Exception) {
                    android.util.Log.w("MainActivity", "queue persist failed: ${e.message}")
                }
            }
        }
        player.onTrackChanged = { name ->
            CoroutineScope(Dispatchers.IO).launch {
                settingsRepo.saveCurrentSong(name)
            }
        }
        // Desktop idle callback: a drained queue refreshes the visible lists.
        queueRepo.onIdle = {
            CoroutineScope(Dispatchers.IO).launch {
                metadataRepo.refreshLibrary()
                syncRepo.refreshLocalList()
                queueRepo.refresh()
            }
        }
        setContent {
            HathorTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    var dest by remember { mutableStateOf(Dest.Library) }
                    // Deep-link into an artist's songs (e.g. history artist
                    // link); consumed once by the All Songs view.
                    var artistTarget by remember { mutableStateOf<String?>(null) }
                    var showNowPlaying by remember { mutableStateOf(false) }
                    var showFirstRun by remember { mutableStateOf(false) }
                    val syncState by syncRepo.state.collectAsState()
                    val wallpaper by settingsRepo.background.collectAsState()
                    // Desktop on_start(): first run (empty library) + remote
                    // configured -> ask whether to load from it. One-shot,
                    // non-dismissable, like the SweetAlert2 prompt.
                    LaunchedEffect(Unit) {
                        launch(Dispatchers.IO) {
                            val prefs = getSharedPreferences("hathor", MODE_PRIVATE)
                            val empty = try {
                                AppDatabase.get(applicationContext).songDao().getAll().isEmpty()
                            } catch (_: Exception) {
                                false
                            }
                            if (!prefs.getBoolean("remote_prompt_done", false) &&
                                BuildConfig.DB_HOST.isNotBlank() && empty
                            ) {
                                withContext(Dispatchers.Main) { showFirstRun = true }
                            }
                        }
                    }
                    fun dismissFirstRun() {
                        getSharedPreferences("hathor", MODE_PRIVATE).edit()
                            .putBoolean("remote_prompt_done", true).apply()
                        showFirstRun = false
                    }

                    // Desktop launch resume: custom manual queue restores
                    // verbatim first, else rebuild from the persisted source,
                    // else the general library. Silent, no autoplay.
                    LaunchedEffect(Unit) {
                        launch(Dispatchers.IO) {
                            try {
                                restorePlayback()
                            } catch (e: Exception) {
                                android.util.Log.w("MainActivity", "restore failed: ${e.message}")
                            }
                        }
                    }
                    val currentFile by player.current.collectAsState()
                    val isPlaying by player.playing.collectAsState()
                    var nowTitle by remember { mutableStateOf<String?>(null) }
                    var nowArtist by remember { mutableStateOf<String?>(null) }
                    var nowCover by remember { mutableStateOf<android.graphics.Bitmap?>(null) }
                    val scope = rememberCoroutineScope()

                    // Resolve display tags + cover, log played history (like playing_view).
                    // Podcasts stay out of the music history (daily mix, recents).
                    LaunchedEffect(currentFile) {
                        val f = currentFile
                        if (f == null) {
                            nowTitle = null
                            nowArtist = null
                            nowCover = null
                        } else {
                            scope.launch(Dispatchers.IO) {
                                // Overlay/display resolution must never break
                                // playback: log-and-continue on any failure.
                                try {
                                    val isPod = try {
                                        f.canonicalPath.startsWith(
                                            PlayerApp.instance.podcastsDir.canonicalPath
                                        )
                                    } catch (_: Exception) {
                                        false
                                    }
                                    val meta = metadataRepo.metaFor(f)
                                    if (!isPod) historyRepo.recordPlayed(f.name)
                                    // File-based lookup resolves each queue
                                    // item to the correct folder's cover.
                                    val cover = metadataRepo.coverArtFor(f, 192)
                                    withContext(Dispatchers.Main) {
                                        nowTitle = meta.title
                                        nowArtist = meta.artist
                                        nowCover = cover
                                    }
                                } catch (e: Exception) {
                                    android.util.Log.w("MainActivity", "now-playing resolve failed: ${e.message}")
                                }
                            }
                        }
                    }

                    HathorShell(
                        player = player,
                        nowPlayingTitle = nowTitle,
                        nowPlayingArtist = nowArtist,
                        nowPlayingCover = nowCover,
                        selected = dest,
                        onSelect = { dest = it },
                        onBarClick = { if (currentFile != null) showNowPlaying = true },
                        wallpaper = wallpaper
                    ) {
                        // Desktop view switch (jQuery load): crossfade + slide.
                        AnimatedContent(
                            targetState = dest,
                            transitionSpec = {
                                (fadeIn(animationSpec = HathorMotion.tween300()) +
                                    slideInHorizontally(
                                        animationSpec = HathorMotion.tween300(),
                                        initialOffsetX = { it / 12 }
                                    )) togetherWith
                                    (fadeOut(animationSpec = HathorMotion.tween300()) +
                                        slideOutHorizontally(
                                            animationSpec = HathorMotion.tween300(),
                                            targetOffsetX = { -it / 12 }
                                        ))
                            },
                            label = "destination"
                        ) { target ->
                        when (target) {
                            Dest.Library -> com.musicplayer.android.ui.HomeScreen(
                                dailyMix = dailyMixRepo,
                                library = metadataRepo,
                                onOpenAllSongs = { dest = Dest.AllSongs },
                                onOpenDailyMix = { dest = Dest.DailyMix },
                                onOpenHistory = { dest = Dest.History },
                                onPlay = { song, visible, source ->
                                    playWithContext(song.file, visible, null, source)
                                },
                                onPlayNext = { song -> queueFile(song.file, next = true) },
                                onEnqueue = { song -> queueFile(song.file, next = false) },
                                nowPlayingFile = currentFile?.name,
                                isPlaying = isPlaying
                            )
                            Dest.AllSongs -> com.musicplayer.android.ui.AllSongsScreen(
                                repo = metadataRepo,
                                playlistRepo = playlistRepo,
                                artwork = artworkRepo,
                                initialArtist = artistTarget,
                                onInitialConsumed = { artistTarget = null },
                                onBack = { artistTarget = null; dest = Dest.Library },
                                onPlayAll = { songs, source ->
                                    if (songs.isNotEmpty()) {
                                        playWithContext(songs.first().file, songs, null, source)
                                    }
                                },
                                onPlay = { song, visible, source ->
                                    playWithContext(song.file, visible, null, source)
                                },
                                onPlayNext = { song -> queueFile(song.file, next = true) },
                                onEnqueue = { song -> queueFile(song.file, next = false) },
                                nowPlayingFile = currentFile?.name,
                                isPlaying = isPlaying
                            )
                            Dest.DailyMix -> com.musicplayer.android.ui.DailyMixScreen(
                                dailyMix = dailyMixRepo,
                                library = metadataRepo,
                                onBack = { dest = Dest.Library },
                                onPlay = { song, visible, date ->
                                    playWithContext(
                                        song.file, visible, null,
                                        com.musicplayer.android.playback.PlaybackSource("daily_mix", date)
                                    )
                                },
                                onPlayAll = { songs, date, shuffle ->
                                    playDailyMix(songs, date, shuffle)
                                },
                                onPlayNext = { song -> queueFile(song.file, next = true) },
                                onEnqueue = { song -> queueFile(song.file, next = false) },
                                nowPlayingFile = currentFile?.name,
                                isPlaying = isPlaying
                            )
                            Dest.Download -> DownloadScreen(
                                queueRepo,
                                engine,
                                previewPlayer,
                                // A preview must never play over the library.
                                onPreviewStarted = { player.pause() }
                            )
                            Dest.Podcasts -> com.musicplayer.android.ui.PodcastsScreen(
                                repo = podcastRepo,
                                metadata = metadataRepo,
                                onPlay = { ep, visible ->
                                    // Desktop _podPlay: queue from the visible
                                    // episodes; source carries the filename.
                                    playWithContext(
                                        ep.file, visible, null,
                                                                                com.musicplayer.android.playback.PlaybackSource("podcast", ep.file)
                                    )
                                },                                onPlayAll = { visible ->
                                    if (visible.isNotEmpty()) {
                                        playWithContext(
                                            visible.first().file, visible, null,
                                            com.musicplayer.android.playback.PlaybackSource(
                                                "podcast", visible.first().file
                                            )
                                        )
                                    }
                                },
                                onPlayNext = { ep ->
                                    previewPlayer.stop()
                                    val f = resolveFile(ep)
                                    if (f.exists()) player.playNext(f)
                                },
                                onEnqueue = { ep ->
                                    previewPlayer.stop()
                                    val f = resolveFile(ep)
                                    if (f.exists()) player.addToQueue(f)
                                },
                                nowPlayingFile = currentFile?.name,
                                isPlaying = isPlaying,
                                isCurrentlyPlaying = { isPlaying },
                                onTogglePause = { player.toggle() },
                                onSeekTo = { ep, secs ->
                                    // Chapter time-chip: play the episode, then
                                    // land on the chapter (pending-seek path).
                                    previewPlayer.stop()
                                    val f = resolveFile(ep)
                                    if (f.exists()) player.playFileAtPosition(f, (secs * 1000).toInt())
                                }
                            )
                            Dest.Playlists -> PlaylistsScreen(
                                repo = playlistRepo,
                                musicDir = engine.outputDir(),
                                onPlayFiles = { files, playlistId ->
                                    previewPlayer.stop()
                                    player.playFiles(files, 0)
                                    CoroutineScope(Dispatchers.IO).launch {
                                        settingsRepo.saveQueueSource(
                                            com.musicplayer.android.playback.PlaybackSource(
                                                "playlist", playlistId.toString()
                                            )
                                        )
                                    }
                                },
                                onPlaySong = { song, visible, playlistId ->
                                    // Desktop playlist row: queue from the
                                    // playlist songs with the playlist id.
                                    playWithContext(
                                        song.file,
                                        visible,
                                        null,
                                        com.musicplayer.android.playback.PlaybackSource(
                                            "playlist", playlistId.toString()
                                        )
                                    )
                                },
                                onPlayNext = { song -> queueFile(song.file, next = true) },
                                onEnqueue = { song -> queueFile(song.file, next = false) }
                            )
                            Dest.History -> HistoryScreen(
                                repo = historyRepo,
                                queue = queueRepo,
                                musicDir = engine.outputDir(),
                                onPlayFile = { file -> playSong(file.name) },
                                onOpenArtist = { name ->
                                    artistTarget = name
                                    dest = Dest.AllSongs
                                }
                            )
                            Dest.Settings -> SettingsScreen(settingsRepo, syncRepo, podcastRepo, onPull = { enqueuePull() })
                        }
                        }
                    }

                    if (showNowPlaying && currentFile != null) {
                        val volume by settingsRepo.volume.collectAsState()
                        val sheetScope = rememberCoroutineScope()
                        NowPlayingSheet(
                            player = player,
                            metadata = metadataRepo,
                            lyrics = lyricsRepo,
                            volume = volume,
                            onVolumeChange = { settingsRepo.setVolume(it) },
                            onVolumeDone = { sheetScope.launch { settingsRepo.persistVolume() } },
                            onDismiss = { showNowPlaying = false }
                        )
                    }

                    // Desktop _prompt_remote_sync(): blocking first-run question.
                    // Yes starts a BACKGROUND pull (notification shows progress),
                    // so nothing ever waits on downloads here.
                    if (showFirstRun) {
                        AlertDialog(
                            onDismissRequest = {},
                            title = { Text("Remote Library Found") },
                            text = {
                                Column {
                                    Text("A remote database is configured. Would you like to load your music library from it?")
                                    Text(
                                        "This syncs songs, playlists, lyrics, and history, then downloads missing songs in the background. You can keep using the app.",
                                        style = MaterialTheme.typography.bodySmall,
                                        color = HathorColors.TextHint
                                    )
                                }
                            },
                            confirmButton = {
                                Button(
                                    onClick = {
                                        enqueuePull()
                                        dismissFirstRun()
                                    },
                                    colors = ButtonDefaults.buttonColors(
                                        containerColor = HathorColors.Accent,
                                        contentColor = androidx.compose.ui.graphics.Color.White
                                    )
                                ) { Text("Yes, load from remote") }
                            },
                            dismissButton = {
                                TextButton(onClick = { dismissFirstRun() }) { Text("No, start fresh") }
                            }
                        )
                    }
                }
            }
        }
    }

    /**
     * Desktop load_current_song() + _rebuild_queue_from_source(): restore a
     * persisted customized queue verbatim first; else rebuild from the
     * persisted source (playlist / today's mix / artist / album / recents /
     * single podcast file). A stale source (deleted playlist, reset mix,
     * renamed artist, missing file) falls back to the general library.
     * Corrupt/legacy rows degrade gracefully, never crash.
     */
    private suspend fun restorePlayback() {
        val dir = engine.outputDir()
        val podDir = try {
            PlayerApp.instance.podcastsDir
        } catch (_: Exception) {
            null
        }

        fun resolveQueued(q: SettingsRepository.QueuedFile): File? {
            val primary = File(if (q.isPodcast) podDir ?: dir else dir, q.file)
            if (primary.exists()) return primary
            val alt = File(if (q.isPodcast) dir else podDir ?: dir, q.file)
            return if (alt.exists()) alt else null
        }

        // 1) Custom manual queue, verbatim (flags intact).
        try {
            val restored = settingsRepo.loadQueue()
            if (restored != null) {
                val files = restored.files.mapNotNull { resolveQueued(it) }
                if (files.isNotEmpty()) {
                    val want = restored.files.getOrNull(restored.index)?.file
                    val idx = files.indexOfFirst { it.name == want }.coerceAtLeast(0)
                    player.prepareFiles(files, idx)
                    return
                }
            }
        } catch (_: Exception) {
        }

        // 2) Rebuild from the persisted source around the current song.
        val currentName = try {
            settingsRepo.loadCurrentSongName()
        } catch (_: Exception) {
            null
        }
        if (currentName.isNullOrBlank()) return
        try {
            metadataRepo.refreshLibrary()
        } catch (_: Exception) {
        }
        val source = try {
            settingsRepo.loadQueueSource()
        } catch (_: Exception) {
            null
        }
        val general = generalLibraryFiles()
        val rebuilt: List<File>? = try {
            when (source?.type) {
                "playlist" -> {
                    val pid = source.id?.toLongOrNull()
                    if (pid == null) null
                    else playlistRepo.songsIn(pid, dir).map { File(dir, it.file) }
                        .filter { it.exists() }.ifEmpty { null }
                }
                "daily_mix" -> {
                    // The mix resets daily: rebuild from TODAY's mix and
                    // refresh the stored date.
                    val mix = dailyMixRepo.getDailyMix()
                    try {
                        settingsRepo.saveQueueSource(
                            com.musicplayer.android.playback.PlaybackSource("daily_mix", mix.date)
                        )
                    } catch (_: Exception) {
                    }
                    mix.songs.map { File(dir, it.file) }.filter { it.exists() }.ifEmpty { null }
                }
                "artist" -> {
                    val name = source.id
                    if (name.isNullOrBlank()) null
                    else metadataRepo.library.value.filter { it.artist == name }
                        .map { File(dir, it.file) }.filter { it.exists() }.ifEmpty { null }
                }
                "album" -> {
                    val name = source.id
                    if (name.isNullOrBlank()) null
                    else metadataRepo.library.value.filter { it.album == name }
                        .map { File(dir, it.file) }.filter { it.exists() }.ifEmpty { null }
                }
                "recently_played" -> {
                    historyRepo.played(dir).mapNotNull { item ->
                        File(dir, item.file).takeIf { it.exists() }
                    }.distinctBy { it.absolutePath }.ifEmpty { null }
                }
                "recently_downloaded" -> {
                    historyRepo.downloaded(dir).mapNotNull { item ->
                        File(dir, item.file).takeIf { it.exists() }
                    }.distinctBy { it.absolutePath }.ifEmpty { null }
                }
                "podcast" -> {
                    // Single-episode queue; a missing file falls back.
                    val f = if (podDir != null) File(podDir, currentName) else null
                    if (f != null && f.exists()) listOf(f) else null
                }
                else -> null
            }
        } catch (_: Exception) {
            null
        }
        if (rebuilt != null && rebuilt.any { it.name == currentName }) {
            player.prepareFiles(rebuilt, rebuilt.indexOfFirst { it.name == currentName })
            return
        }

        // 3) General library fallback.
        if (general.isNotEmpty()) {
            val idx = general.indexOfFirst { it.name == currentName }.coerceAtLeast(0)
            player.prepareFiles(general, idx)
        }
    }

    /** Resolve a SongMeta to its File (songs folder vs podcasts folder). */
    private fun resolveFile(song: com.musicplayer.android.data.SongMeta): File {
        val dir = if (song.isPodcast) engine.podcastsDir() else engine.outputDir()
        val direct = File(dir, song.file)
        if (direct.exists()) return direct
        // Cross-dir fallback (a podcast row that landed in songs, or vice versa).
        val other = if (song.isPodcast) engine.outputDir() else engine.podcastsDir()
        val alt = File(other, song.file)
        return if (alt.exists()) alt else direct
    }

    private fun resolveFileByName(fileName: String): File {
        val direct = File(engine.outputDir(), fileName)
        if (direct.exists()) return direct
        val alt = File(engine.podcastsDir(), fileName)
        return if (alt.exists()) alt else direct
    }

    /**
     * Queue-building fallback chain (desktop row-click / bento-Play logic):
     * playlist songs -> current-view songs -> general library.
     * Row clicks and the context menu's Play action build the queue from the
     * visible list plus the view's playback context.
     */
    private fun playWithContext(
        fileName: String,
        viewSongs: List<com.musicplayer.android.data.SongMeta>?,
        playlistFiles: List<File>?,
        source: com.musicplayer.android.playback.PlaybackSource?
    ) {
        previewPlayer.stop()
        CoroutineScope(Dispatchers.IO).launch {
            val queue: List<File> = when {
                !playlistFiles.isNullOrEmpty() ->
                    playlistFiles.filter { it.exists() }

                !viewSongs.isNullOrEmpty() -> {
                    val mapped = viewSongs.map { resolveFile(it) }.filter { it.exists() }
                    if (mapped.isNotEmpty()) mapped else generalLibraryFiles()
                }

                else -> generalLibraryFiles()
            }
            val idx = queue.indexOfFirst { it.name == fileName }.coerceAtLeast(0)
            if (queue.isEmpty()) {
                val single = resolveFileByName(fileName)
                withContext(Dispatchers.Main) { player.playFile(single) }
            } else {
                withContext(Dispatchers.Main) {
                    if (idx in queue.indices) player.playFiles(queue, idx)
                    else player.playFiles(queue, 0)
                }
            }
            // Persist the playback context backend-side (desktop _persist_source).
            try {
                settingsRepo.saveQueueSource(source)
            } catch (_: Exception) {
            }
        }
    }

    private fun generalLibraryFiles(): List<File> {
        val dir = engine.outputDir()
        val cached = try {
            metadataRepo.library.value.map { it.file }
        } catch (_: Exception) {
            emptyList()
        }
        if (cached.isNotEmpty()) {
            val files = cached.mapNotNull { name -> File(dir, name).takeIf { it.exists() } }
            if (files.isNotEmpty()) return files
        }
        return dir.listFiles { fl ->
            fl.isFile && fl.extension.equals("mp3", ignoreCase = true)
        }?.sortedBy { it.name } ?: emptyList()
    }

    /** Daily Mix Play / Shuffle-play (shuffled variant builds its own order). */
    private fun playDailyMix(
        songs: List<com.musicplayer.android.data.SongMeta>,
        date: String?,
        shuffle: Boolean
    ) {
        if (songs.isEmpty()) return
        previewPlayer.stop()
        val ordered = if (shuffle) songs.shuffled() else songs
        val first = ordered.first()
        playWithContext(
            first.file, ordered, null,
            com.musicplayer.android.playback.PlaybackSource("daily_mix", date)
        )
    }

    private fun playSong(fileName: String) {
        // Legacy entry points (playlist/history rows without a visible list):
        // fall back to the general library chain with no source (null =
        // playlist/general fallback logic, like desktop).
        playWithContext(fileName, null, null, null)
    }

    /** Desktop next_to_queue / add_to_queue per-song actions. */
    private fun queueFile(fileName: String, next: Boolean) {
        previewPlayer.stop()
        val target = resolveFileByName(fileName)
        if (!target.exists()) return
        if (next) player.playNext(target) else player.addToQueue(target)
    }

    override fun onDestroy() {
        // Player is process-lifetime (service-owned); nothing to release here.
        super.onDestroy()
    }

    /** Pull-once worker (also used by the Settings Pull button). */
    fun enqueuePull() {
        val req = OneTimeWorkRequestBuilder<PullWorker>()
            .setConstraints(Constraints(requiredNetworkType = NetworkType.CONNECTED))
            .build()
        WorkManager.getInstance(this).enqueueUniqueWork(
            PullWorker.UNIQUE_NAME,
            ExistingWorkPolicy.REPLACE,
            req
        )
    }

    /** Media notification needs runtime consent on API 33+; playback works regardless. */
    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT < 33) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED
        ) return
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { }.launch(
            Manifest.permission.POST_NOTIFICATIONS
        )
    }
}
