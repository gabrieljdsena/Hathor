package com.musicplayer.android.data

// Lightweight song/episode handle passed between screens and the player
// (desktop song dicts: File/Title/Artist/Album/Year/Duration + folder flag).
// Full tag reads stay in MetadataRepository; this is the list-row shape.
data class SongMeta(
    val file: String,
    val title: String,
    val artist: String,
    val album: String,
    val year: String = "Unknown",
    val durationSec: Long,
    val dateDownload: String?,
    val hasCover: Boolean = false,
    // Joined context (not tags): playlist add-date for DateAdded sorts.
    val dateAdded: String? = null,
    // Second library dir flag (desktop IsPodcast): songs folder vs podcasts folder.
    val isPodcast: Boolean = false,
)
