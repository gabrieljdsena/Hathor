package com.musicplayer.android.data.remote

import android.content.Context
import android.util.Log
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters

/**
 * Background push (desktop DatabaseSync 30s thread equivalent, adapted for
 * mobile: WorkManager periodic, network-required, instead of a raw thread).
 * Manual pushes go through SyncRepository.pushToRemote() with visible status.
 */
class SyncWorker(appContext: Context, params: WorkerParameters) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result {
        val config = RemoteConfig()
        if (!config.isConfigured) {
            Log.i(TAG, "Remote DB not configured; skipping background push")
            return Result.success()
        }
        return try {
            RemoteUpload(applicationContext, config).run()
            Log.i(TAG, "Background push complete")
            Result.success()
        } catch (e: Exception) {
            Log.w(TAG, "Background push failed, will retry: ${e.message}")
            Result.retry()
        }
    }

    companion object {
        private const val TAG = "SyncWorker"
        const val UNIQUE_NAME = "hathor-remote-push"
    }
}
