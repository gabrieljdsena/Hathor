package com.musicplayer.android.data

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.util.Log
import com.musicplayer.android.MainActivity
import com.musicplayer.android.PlayerApp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Keeps downloads alive with the screen locked / under Doze: a dataSync
 * foreground service driven by the queue's active count. Started by the
 * queue when work begins; stops itself the moment nothing is active.
 */
class DownloadService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var startedForeground = false

    override fun onCreate() {
        super.onCreate()
        ensureChannel()
        val queue = (applicationContext as PlayerApp).queueRepo
        scope.launch {
            var everActive = false
            queue.activeCount.collect { active ->
                if (active > 0) {
                    everActive = true
                    push(active)
                    if (!startedForeground) {
                        try {
                            startForeground(NOTIF_ID, build(active))
                            startedForeground = true
                        } catch (e: Exception) {
                            Log.w(TAG, "startForeground failed: ${e.message}")
                        }
                    }
                } else if (startedForeground) {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    startedForeground = false
                    stopSelf()
                } else if (!everActive) {
                    // Started with no work (stray start): don't linger.
                    stopSelf()
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Starts are edge-triggered from the queue; state comes from the flow.
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun push(active: Int) {
        try {
            getSystemService(NotificationManager::class.java).notify(NOTIF_ID, build(active))
        } catch (e: SecurityException) {
            Log.w(TAG, "No notification permission: ${e.message}")
        }
    }

    private fun build(active: Int): Notification {
        val intent = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Hathor downloads")
            .setContentText(if (active == 1) "Downloading 1 song…" else "Downloading $active songs…")
            .setContentIntent(intent)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
    }

    private fun ensureChannel() {
        val mgr = getSystemService(NotificationManager::class.java)
        if (mgr.getNotificationChannel(CHANNEL_ID) == null) {
            mgr.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Downloads", NotificationManager.IMPORTANCE_LOW)
            )
        }
    }

    companion object {
        private const val TAG = "DownloadService"
        private const val CHANNEL_ID = "hathor_downloads"
        private const val NOTIF_ID = 3

        fun ensure(context: android.content.Context) {
            try {
                context.startForegroundService(Intent(context, DownloadService::class.java))
            } catch (e: Exception) {
                Log.w(TAG, "ensure failed: ${e.message}")
            }
        }
    }
}
