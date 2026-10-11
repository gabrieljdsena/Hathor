package com.musicplayer.android.data

import android.content.Context
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncApiException
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.remote.RemoteWriter
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext

// Manual-only sync (no background threads/timers): push runs here when the
// Settings Push button is tapped; pull runs in PullWorker (first-run prompt
// + Pull button). Semantics mirror desktop api_sync push: rows + tombstones
// via POST import, missing bytes via PUT, tombstones cleared only on
// server accept.
class SyncRepository(
    context: Context,
    private val engine: DownloadEngine,
) {
    private val appContext = context.applicationContext
    private val db: AppDatabase by lazy { AppDatabase.get(appContext) }

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

    suspend fun pushToRemote() {
        if (_state.value is SyncState.Running) return
        _state.value = SyncState.Running("Connecting to sync server…")
        try {
            if (!SyncConfig.isConfigured()) {
                _state.value = SyncState.Error(SyncConfig.NOT_CONFIGURED)
                return
            }
            val api = SyncApi(SyncConfig.baseUrl(), SyncConfig.token())
            val songsPushed = withContext(Dispatchers.IO) {
                _state.value = SyncState.Running("Pushing songs, playlists and lyrics…")
                RemoteWriter.pushSongs(db, engine, api)
            }
            val podcastsPushed = withContext(Dispatchers.IO) {
                _state.value = SyncState.Running("Pushing podcasts and tags…")
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
