package com.musicplayer.android.playback

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.IBinder
import android.util.Log
import com.musicplayer.android.MainActivity
import com.musicplayer.android.PlayerApp
import com.musicplayer.android.data.MetadataRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Foreground playback service (desktop windows_media.py SMTC overlay
 * equivalent): framework MediaSession for BT/lock-screen controls + a
 * MediaStyle notification with prev/play/next, working with the screen off
 * and outside the app. The shared process-lifetime PlayerManager (held by
 * PlayerApp) does the actual playback; this service only surfaces it.
 */
class PlayerService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var player: PlayerManager
    private lateinit var metadata: MetadataRepository
    private var session: MediaSession? = null

    private var cachedTitle: String? = null
    private var cachedArtist: String? = null
    private var cachedArt: Bitmap? = null
    private var lastPlaying: Boolean? = null

    override fun onCreate() {
        super.onCreate()
        player = PlayerApp.instance.player
        metadata = MetadataRepository(this, PlayerApp.instance.downloadEngine)
        createChannel()
        session = MediaSession(this, "Hathor").apply {
            setCallback(object : MediaSession.Callback() {
                override fun onPlay() {
                    if (!player.playing.value) player.toggle()
                }

                override fun onPause() {
                    if (player.playing.value) player.toggle()
                }

                override fun onSkipToNext() = player.next()
                override fun onSkipToPrevious() = player.prev()
                override fun onSeekTo(pos: Long) = player.seekTo(pos.toInt())
            })
            isActive = true
        }
        scope.launch { player.playing.collect { onPlaybackChanged(it) } }
        scope.launch {
            player.current.collect { file ->
                scope.launch(Dispatchers.IO) {
                    // A flaky OS overlay must never break playback or saves:
                    // log-and-continue, never propagate.
                    try {
                        if (file == null) {
                            cachedTitle = null
                            cachedArtist = null
                            cachedArt = null
                        } else {
                            val meta = metadata.metaFor(file)
                            cachedTitle = meta.title
                            cachedArtist = meta.artist
                            cachedArt = metadata.coverArtFor(file, 512)
                        }
                    } catch (e: Exception) {
                        Log.w(TAG, "overlay metadata resolve failed: ${e.message}")
                    }
                    updateSession()
                    pushNotification(player.playing.value)
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_TOGGLE -> player.toggle()
            ACTION_NEXT -> {
                ensureQueue()
                player.next()
            }
            ACTION_PREV -> {
                ensureQueue()
                player.prev()
            }
        }
        // If we were (re)started while something plays, go foreground now.
        if (player.playing.value) pushNotification(true)
        return START_STICKY
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        // App swiped away while paused: no reason to linger.
        if (!player.playing.value) stopSelf()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        session?.release()
        session = null
        scope.cancel()
        super.onDestroy()
    }

    private fun ensureQueue() {
        if (player.queueFlow.value.isEmpty()) player.refreshQueue()
    }

    private fun onPlaybackChanged(playing: Boolean) {
        if (playing == lastPlaying) return
        lastPlaying = playing
        updateSession()
        pushNotification(playing)
    }

    private fun updateSession() {
        // Never let a flaky OS overlay break playback of a loaded song.
        try {
            val s = session ?: return
            val file = player.current.value
            val md = MediaMetadata.Builder()
                .putString(MediaMetadata.METADATA_KEY_TITLE, cachedTitle ?: file?.nameWithoutExtension ?: "")
                .putString(MediaMetadata.METADATA_KEY_ARTIST, cachedArtist ?: "")
                .putLong(MediaMetadata.METADATA_KEY_DURATION, player.durationMs.value.toLong())
            cachedArt?.let { md.putBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART, it) }
            s.setMetadata(md.build())
            val state = if (player.playing.value) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED
            s.setPlaybackState(
                PlaybackState.Builder()
                    .setState(state, player.positionMs.value.toLong(), 1f)
                    .setActions(
                        PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or
                            PlaybackState.ACTION_SKIP_TO_NEXT or PlaybackState.ACTION_SKIP_TO_PREVIOUS or
                            PlaybackState.ACTION_SEEK_TO
                    )
                    .build()
            )
        } catch (e: Exception) {
            Log.w(TAG, "overlay session update failed (playback unaffected): ${e.message}")
        }
    }

    private fun pushNotification(playing: Boolean) {
        // Never let a flaky OS overlay break playback of a loaded song.
        try {
            val notif = buildNotification(playing)
            getSystemService(NotificationManager::class.java).notify(NOTIF_ID, notif)
            if (playing) {
                startForeground(NOTIF_ID, notif)
            } else {
                stopForeground(STOP_FOREGROUND_DETACH)
            }
        } catch (e: Exception) {
            Log.w(TAG, "overlay notification failed (playback unaffected): ${e.message}")
        }
    }

    private fun buildNotification(playing: Boolean): Notification {
        val contentIntent = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        fun actionIntent(action: String, code: Int): PendingIntent =
            PendingIntent.getService(
                this, code, Intent(this, PlayerService::class.java).setAction(action),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
        val token = session?.sessionToken
        val builder = Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle(cachedTitle ?: "Hathor")
            .setContentText(cachedArtist ?: "")
            .setContentIntent(contentIntent)
            .setOngoing(playing)
            .setOnlyAlertOnce(true)
            .addAction(android.R.drawable.ic_media_previous, "Previous", actionIntent(ACTION_PREV, 1))
            .addAction(
                if (playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play,
                if (playing) "Pause" else "Play",
                actionIntent(ACTION_TOGGLE, 2)
            )
            .addAction(android.R.drawable.ic_media_next, "Next", actionIntent(ACTION_NEXT, 3))
            .setStyle(
                Notification.MediaStyle()
                    .setShowActionsInCompactView(0, 1, 2)
                    .apply { if (token != null) setMediaSession(token) }
            )
        cachedArt?.let { builder.setLargeIcon(it) }
        return builder.build()
    }

    private fun createChannel() {
        val mgr = getSystemService(NotificationManager::class.java)
        if (mgr.getNotificationChannel(CHANNEL_ID) == null) {
            mgr.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Playback", NotificationManager.IMPORTANCE_LOW)
            )
        }
    }

    companion object {
        private const val TAG = "PlayerService"
        private const val CHANNEL_ID = "hathor_playback"
        private const val NOTIF_ID = 1
        const val ACTION_TOGGLE = "com.musicplayer.android.TOGGLE"
        const val ACTION_NEXT = "com.musicplayer.android.NEXT"
        const val ACTION_PREV = "com.musicplayer.android.PREV"
    }
}
