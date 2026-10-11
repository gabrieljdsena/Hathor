package com.musicplayer.android.data

// Lightweight song/episode handle passed between screens and the player
// (desktop song dicts: File/Title/Artist/Album/Duration + folder flag).
// Full tag reads stay in MetadataRepository; this is the list-row shape.
data class SongMeta(
    val file: String,
    val title: String,
    val artist: String,
    val album: String,
    val durationSec: Int,
    val dateDownload: String?,
    val isPodcast: Boolean,
)
