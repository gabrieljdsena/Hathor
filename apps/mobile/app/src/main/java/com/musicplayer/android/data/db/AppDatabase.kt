package com.musicplayer.android.data.db

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

// Local library database (desktop music_player.db equivalent, per-device).
// Created on first access; pull merges into it, push reads from it.
@Database(
    entities = [
        SongEntity::class,
        PodcastEntity::class,
        PlaylistEntity::class,
        SongPlaylistLink::class,
        PodcastTagEntity::class,
        PodcastTagLink::class,
        LyricEntity::class,
        MusicHistoryEntry::class,
        PlaylistHistoryEntry::class,
        DailyMixEntity::class,
        SyncDeletion::class,
    ],
    version = 1,
    exportSchema = false,
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun songDao(): SongDao
    abstract fun podcastDao(): PodcastDao
    abstract fun playlistDao(): PlaylistDao
    abstract fun songPlaylistDao(): SongPlaylistDao
    abstract fun podcastTagDao(): PodcastTagDao
    abstract fun podcastTagLinkDao(): PodcastTagLinkDao
    abstract fun lyricDao(): LyricDao
    abstract fun musicHistoryDao(): MusicHistoryDao
    abstract fun playlistHistoryDao(): PlaylistHistoryDao
    abstract fun dailyMixDao(): DailyMixDao
    abstract fun syncDeletionDao(): SyncDeletionDao

    companion object {
        @Volatile
        private var instance: AppDatabase? = null

        fun get(context: Context): AppDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "hathor.db",
                ).build().also { instance = it }
            }
    }
}
