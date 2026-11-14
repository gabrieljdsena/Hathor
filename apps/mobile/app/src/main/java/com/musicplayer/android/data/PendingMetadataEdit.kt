package com.musicplayer.android.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Metadata edit staged while its file is still open by the player.
 *
 * Rewriting tags of the file that MediaPlayer currently holds can leave the
 * player reading stale/garbled headers. The editor therefore queues the write
 * and MainActivity applies it on the next track change (same idea as the web
 * client's pending edit). Non-playing files save immediately.
 */
data class PendingMetadataEdit(
    val file: String,
    val title: String,
    val artist: String,
    val album: String,
    val year: String,
    val cover: ByteArray?,
    val removeCover: Boolean
)

object PendingMetadataEdits {
    private val _edit = MutableStateFlow<PendingMetadataEdit?>(null)
    val edit: StateFlow<PendingMetadataEdit?> = _edit.asStateFlow()

    fun stage(edit: PendingMetadataEdit) {
        _edit.value = edit
    }

    fun clear() {
        _edit.value = null
    }

    fun clearFor(file: String) {
        if (_edit.value?.file == file) _edit.value = null
    }
}
