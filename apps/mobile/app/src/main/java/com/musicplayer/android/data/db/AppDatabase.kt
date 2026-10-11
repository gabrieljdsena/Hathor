package com.musicplayer.android.data.db

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase

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
        PodcastChapterEntity::class,
        LyricEntity::class,
        MusicHistoryEntry::class,
        PlaylistHistoryEntry::class,
        DailyMixEntity::class,
        SyncDeletion::class,
    ],
    version = 2,
    exportSchema = false,
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun songDao(): SongDao
    abstract fun podcastDao(): PodcastDao
    abstract fun playlistDao(): PlaylistDao
    abstract fun songPlaylistDao(): SongPlaylistDao
    abstract fun podcastTagDao(): PodcastTagDao
    abstract fun podcastTagLinkDao(): PodcastTagLinkDao
    abstract fun podcastChapterDao(): PodcastChapterDao
    abstract fun lyricDao(): LyricDao
    abstract fun musicHistoryDao(): MusicHistoryDao
    abstract fun playlistHistoryDao(): PlaylistHistoryDao
    abstract fun dailyMixDao(): DailyMixDao
    abstract fun syncDeletionDao(): SyncDeletionDao

    companion object {
        @Volatile
        private var instance: AppDatabase? = null

        // v1 -> v2: podcast chapter marks (desktop Podcast_Chapters parity).
        // offset_ms already ships in the v1 LyricEntity, so no lyrics step.
        val MIGRATION_1_2: Migration = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS `podcast_chapters` (" +
                        "`id` INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "`podcast_file` TEXT NOT NULL, " +
                        "`name` TEXT NOT NULL, " +
                        "`start_secs` REAL NOT NULL, " +
                        "`end_secs` REAL)",
                )
                db.execSQL(
                    "CREATE INDEX IF NOT EXISTS `index_podcast_chapters_podcast_file_start_secs` " +
                        "ON `podcast_chapters` (`podcast_file`, `start_secs`)",
                )
            }
        }

        fun get(context: Context): AppDatabase =
            instance ?: synchronized(this) {
                instance ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "hathor.db",
                ).addMigrations(MIGRATION_1_2).build().also { instance = it }
            }
    }
}
