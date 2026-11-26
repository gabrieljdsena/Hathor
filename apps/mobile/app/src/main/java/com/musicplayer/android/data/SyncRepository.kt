package com.musicplayer.android.data

import android.content.Context
import android.util.Log
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncApiException
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.remote.RemoteWriter
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

// Manual full sync (Settings Push/Pull) plus an automatic, debounced
// Music_History outbox flush after each play. The outbox is durable: rows
// leave it only after the server accepts the import. Pull runs in PullWorker
// (first-run prompt + Pull button). Semantics mirror desktop api_sync push:
// rows + tombstones via POST import, missing bytes via PUT, tombstones cleared
// only on server accept.
class SyncRepository(
    context: Context,
    private val engine: DownloadEngine,
    database: AppDatabase? = null,
) {
    private val appContext = context.applicationContext
    private val db: AppDatabase by lazy { database ?: AppDatabase.get(appContext) }
    private val historyScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val historyPushMutex = Mutex()
    private var historyPushJob: Job? = null

    companion object {
        private const val TAG = "SyncRepository"
        private const val HISTORY_PUSH_DELAY_MS = 750L
    }

    sealed interface SyncState {
        data object Idle : SyncState
        data class Running(val step: String) : SyncState
        data class Done(val summary: String) : SyncState
        data class Error(val message: String) : SyncState
    }

    private val _state = MutableStateFlow<SyncState>(SyncState.Idle)
    val state: StateFlow<SyncState> = _state.asStateFlow()

    /** Bumped after every merge/push so lists reload (MainActivity calls this on launch). */
    private val _libraryGeneration = MutableStateFlow(0)
    val libraryGeneration: StateFlow<Int> = _libraryGeneration.asStateFlow()

    fun refreshLocalList() {
        _libraryGeneration.value += 1
    }

    /**
     * Queue a debounced history flush. Safe to call from playback; it never
     * blocks audio and no-ops when the Web API is not configured.
     */
    fun scheduleHistoryPush(delayMs: Long = HISTORY_PUSH_DELAY_MS) {
        if (!SyncConfig.isConfigured()) return
        historyPushJob?.cancel()
        historyPushJob = historyScope.launch {
            delay(delayMs)
            pushPendingHistory()
        }
    }

    /**
     * Push only queued Music_History rows. The outbox is cleared after the
     * server accepts the import; failures leave it intact for the next
     * play/startup retry.
     */
    suspend fun pushPendingHistory() {
        if (!SyncConfig.isConfigured()) return
        historyPushMutex.withLock {
            val ids = try {
                db.historySyncQueueDao().pendingIds()
            } catch (e: Exception) {
                Log.w(TAG, "history outbox read failed: ${e.message}")
                return@withLock
            }
            if (ids.isEmpty()) return@withLock
            val rows = try {
                db.playlistDao().musicHistoryByIds(ids)
            } catch (e: Exception) {
                Log.w(TAG, "history row read failed: ${e.message}")
                return@withLock
            }
            val found = rows.map { it.id }.toSet()
            val missing = ids.filter { it !in found }
            if (missing.isNotEmpty()) {
                db.historySyncQueueDao().clear(missing)
            }
            if (rows.isEmpty()) return@withLock
            try {
                val api = SyncApi()
                api.postImport(RemoteWriter.historyPayload(rows))
                db.historySyncQueueDao().clear(rows.map { it.id })
            } catch (e: Exception) {
                // Best effort: the outbox is durable and will retry.
                Log.w(TAG, "history push failed: ${e.message}")
            }
        }
    }

    suspend fun pushToRemote() {
        if (_state.value is SyncState.Running) return
        _state.value = SyncState.Running("Connecting to sync serverâ€¦")
        try {
            if (!SyncConfig.isConfigured()) {
                _state.value = SyncState.Error(SyncConfig.NOT_CONFIGURED)
                return
            }
            val api = SyncApi()
            val songsPushed = withContext(Dispatchers.IO) {
                _state.value = SyncState.Running("Pushing songs, playlists and lyricsâ€¦")
                RemoteWriter.pushSongs(db, engine, api)
            }
            val podcastsPushed = withContext(Dispatchers.IO) {
                _state.value = SyncState.Running("Pushing podcasts and tagsâ€¦")
                RemoteWriter.pushPodcasts(db, engine, api)
            }
            val total = songsPushed.rows + podcastsPushed.rows
            refreshLocalList()
            _state.value = SyncState.Done("Pushed $total rows to sync server.")
        } catch (e: Exception) {
            _state.value = SyncState.Error(describe("Error pushing to sync server", e))
        }
    }

    private fun describe(prefix: String, e: Exception): String {
        val msg = e.message.orEmpty()
        return if (e is SyncApiException) {
            msg
        } else if (msg.contains("connect", ignoreCase = true) ||
            msg.contains("host", ignoreCase = true)
        ) {
            "$prefix: $msg (is the server on your LAN and running?)"
        } else {
            "$prefix: $msg"
        }
    }
}
