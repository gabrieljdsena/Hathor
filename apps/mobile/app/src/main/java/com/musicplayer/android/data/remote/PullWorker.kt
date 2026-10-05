package com.musicplayer.android.data.remote

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.remote.RemoteSync.summary
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

// Background pull (first-run prompt + Settings Pull button share this one
// worker via enqueueUniqueWork REPLACE). Reads the shared remote, merges
// into Room (desktop pull 1:1), downloads missing files with exact remote
// filenames, and reports per-song status. Progress surfaces through
// WorkManager state (Settings polls it); never blocks UI.
class PullWorker(
    appContext: Context,
    params: WorkerParameters,
) : CoroutineWorker(appContext, params) {

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        try {
            val context = applicationContext
            val db = AppDatabase.get(context)
            val engine = DownloadEngine(context)
            val report = RemoteSync.pullNow(db, engine)
            Result.success(workDataOf("summary" to report.summary()))
        } catch (e: Exception) {
            val message = e.message?.takeIf { it.isNotBlank() }
                ?: "Background pull failed: unknown error."
            Result.failure(workDataOf("error" to message))
        }
    }

    companion object {
        const val UNIQUE_NAME = "hathor-pull"
    }
}
