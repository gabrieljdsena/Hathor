package com.musicplayer.android.data

import android.content.Context
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.data.remote.RemoteDb
import com.musicplayer.android.data.remote.RemoteWriter
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext

// Manual-only sync (no background threads/timers): push runs here when the
// Settings Push button is tapped; pull runs in PullWorker (first-run prompt
// + Pull button). Semantics mirror desktop sync.py + web RemotePushService:
// schema init, upserts, scoped link replace, tombstone propagation,
// newest-wins mix + prune, incremental history past remote MAX(id).
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
        _state.value = SyncState.Running("Connecting to remote DB…")
        try {
            if (!RemoteDb.isConfigured()) {
                _state.value = SyncState.Error("No remote DB configured. Remote sync unavailable.")
                return
            }
            val songsPushed = withContext(Dispatchers.IO) {
                _state.value = SyncState.Running("Pushing songs, playlists and lyrics…")
                RemoteWriter.pushSongs(db)
            }
            val podcastsPushed = withContext(Dispatchers.IO) {
                _state.value = SyncState.Running("Pushing podcasts and tags…")
                RemoteWriter.pushPodcasts(db)
            }
            val total = songsPushed.rows + podcastsPushed.rows
            refreshLocalList()
            _state.value = SyncState.Done("Pushed $total rows to remote DB.")
        } catch (e: Exception) {
            _state.value = SyncState.Error(describe("Error pushing to remote DB", e))
        }
    }

    private fun describe(prefix: String, e: Exception): String {
        val msg = e.message.orEmpty()
        return if (msg.contains("connect timeout", ignoreCase = true)) {
            "$prefix: $msg (the remote cluster may be waking from sleep — try again in a minute)."
        } else {
            "$prefix: $msg"
        }
    }
}
