package com.musicplayer.android.data.local

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.room.Room
import androidx.room.RoomDatabase
import android.content.Context

/**
 * Phase 2: local schema mirrors desktop `database.sql` exactly — same table
 * names, same column names, same primary keys — so any export stays compatible.
 */

// ---- Songs(file PK, downloaded_link, title, date_download, artist) ----
@Entity(tableName = "Songs")
data class SongEntity(
    @PrimaryKey val file: String,
    @ColumnInfo(name = "downloaded_link") val downloadedLink: String?,
    val title: String,
    @ColumnInfo(name = "date_download") val dateDownload: String?,
    val artist: String?
)

@Entity(tableName = "Playlists")
data class PlaylistEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val title: String,
    val description: String?,
    val thumbnail: String?
)

@Entity(tableName = "Song_Playlist")
data class SongPlaylistEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "song_file") val songFile: String,
    @ColumnInfo(name = "playlist_id") val playlistId: Long,
    @ColumnInfo(name = "date_added") val dateAdded: String?
)

@Entity(tableName = "Lyrics")
data class LyricEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "song_file") val songFile: String,
    val lyrics: String?,
    // Highlight-timing correction (desktop offset_ms / web ±20000ms).
    @ColumnInfo(name = "offset_ms") val offsetMs: Int = 0
)

@Entity(tableName = "Music_History")
data class MusicHistoryEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "song_file") val songFile: String,
    @ColumnInfo(name = "date_played") val datePlayed: String?
)

@Entity(tableName = "Playlist_History")
data class PlaylistHistoryEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "playlist_id") val playlistId: Long,
    @ColumnInfo(name = "date_played") val datePlayed: String?
)

// Persisted download job log (mirrors desktop Download_Queue: qid unique,
// url/title/artist, status, progress, error, filename, timestamps).
@Entity(
    tableName = "Download_Queue",
    indices = [Index(value = ["qid"], unique = true)]
)
data class DownloadJobEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val qid: String,
    val url: String?,
    val title: String?,
    val artist: String?,
    val status: String = "queued",
    val progress: Float = 0f,
    val error: String? = null,
    val filename: String? = null,
    @ColumnInfo(name = "created_at") val createdAt: String? = null,
    @ColumnInfo(name = "updated_at") val updatedAt: String? = null,
    // Desktop Download_Queue.is_podcast: routes the output to the podcasts
    // folder instead of the songs folder.
    @ColumnInfo(name = "is_podcast") val isPodcast: Int = 0
)

// Second library dir (desktop Podcasts table mirror: file PK,
// downloaded_link, title, date_download, artist).
@Entity(tableName = "Podcasts")
data class PodcastEntity(
    @PrimaryKey val file: String,
    @ColumnInfo(name = "downloaded_link") val downloadedLink: String?,
    val title: String,
    @ColumnInfo(name = "date_download") val dateDownload: String?,
    val artist: String?
)

// Chapter marks inside a podcast episode (desktop Podcast_Chapters + web
// PodcastTimestamp, 1:1): start offset in seconds, display name, optional
// end offset (null = open-ended, never auto-skipped).
@Entity(
    tableName = "Podcast_Chapters",
    indices = [Index(value = ["podcast_file", "start_secs"])]
)
data class PodcastChapterEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "podcast_file") val podcastFile: String,
    val name: String,
    @ColumnInfo(name = "start_secs") val startSecs: Double,
    @ColumnInfo(name = "end_secs") val endSecs: Double?
)

// Podcast tags (new feature, same shape as Playlists + Song_Playlist):
// Podcast_Tags(id PK, unique name) + Podcast_Tag_Links(id PK,
// podcast_file + tag_id, unique pair). Remote mirrors are lowercase
// (podcast_tags / podcast_tag_links), like every other table pair.
@Entity(
    tableName = "Podcast_Tags",
    indices = [Index(value = ["name"], unique = true)]
)
data class PodcastTagEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val name: String
)

@Entity(
    tableName = "Podcast_Tag_Links",
    indices = [Index(value = ["podcast_file", "tag_id"], unique = true)]
)
data class PodcastTagLinkEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "podcast_file") val podcastFile: String,
    @ColumnInfo(name = "tag_id") val tagId: Long
)

// Daily mix cache (desktop Daily_Mix: mix_date PK YYYY-MM-DD, song_files JSON,
// created_at). One row per day; generation purges non-today rows.
@Entity(tableName = "Daily_Mix")
data class DailyMixEntity(
    @PrimaryKey @ColumnInfo(name = "mix_date") val mixDate: String,
    @ColumnInfo(name = "song_files") val songFiles: String,
    @ColumnInfo(name = "created_at") val createdAt: String? = null
)

// Lean mirror of the desktop Settings row (id=1). Only columns with a mobile
// meaning are kept: playback volume, download concurrency, music folder,
// retained current song/playlist. Window/background-bundled fields stay desktop-only.
@Entity(tableName = "Settings")
data class SettingsEntity(
    @PrimaryKey val id: Int = 1,
    @ColumnInfo(name = "current_volume") val currentVolume: Float? = null,
    @ColumnInfo(name = "limit_downloads") val limitDownloads: Int? = null,
    @ColumnInfo(name = "songs_path") val songsPath: String? = null,
    @ColumnInfo(name = "background_path") val backgroundPath: String? = null,
    @ColumnInfo(name = "current_song") val currentSong: String? = null,
    @ColumnInfo(name = "current_playlist") val currentPlaylist: Long? = null,
    // Desktop queue persistence (Settings.queue_songs/custom_queue).
    @ColumnInfo(name = "queue_songs") val queueSongs: String? = null,
    @ColumnInfo(name = "custom_queue") val customQueue: Int? = null,
    // Desktop playback context (Settings.queue_source JSON
    // {'type':..., 'id':...} or null) + podcasts folder (podcasts_path).
    @ColumnInfo(name = "queue_source") val queueSource: String? = null,
    @ColumnInfo(name = "podcasts_path") val podcastsPath: String? = null,
    // Desktop crossfade prefs (local-only, never synced — the remote schema
    // mirrors songs/podcasts/playlists/links/lyrics/history/daily_mix only).
    @ColumnInfo(name = "crossfade_enabled") val crossfadeEnabled: Int? = null,
    @ColumnInfo(name = "crossfade_seconds") val crossfadeSeconds: Float? = null
)

// Tombstones for rows deleted locally, so a future upload-sync propagates them
// (mirrors desktop Sync_Deletions + DatabaseManager.record_deletion).
@Entity(tableName = "Sync_Deletions")
data class SyncDeletionEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "table_name") val tableName: String,
    @ColumnInfo(name = "row_key") val rowKey: String,
    @ColumnInfo(name = "deleted_at") val deletedAt: String? = null
)

@Dao
interface SongDao {
    @Query("SELECT * FROM Songs ORDER BY title")
    suspend fun getAll(): List<SongEntity>

    @Query("SELECT * FROM Songs WHERE file = :file LIMIT 1")
    suspend fun getByFile(file: String): SongEntity?

    // INSERT OR IGNORE semantics for the "added_count" diff, like desktop.
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(song: SongEntity): Long

    @Query("UPDATE Songs SET title = :title, artist = :artist WHERE file = :file")
    suspend fun updateTitleArtist(file: String, title: String, artist: String)

    @Query("UPDATE Songs SET downloaded_link = :link, date_download = :date WHERE file = :file")
    suspend fun updateDownloadedLink(file: String, link: String, date: String)

    @Query("DELETE FROM Song_Playlist WHERE song_file = :file")
    suspend fun deleteLinks(file: String)

    @Query("DELETE FROM Lyrics WHERE song_file = :file")
    suspend fun deleteLyrics(file: String)

    @Query("DELETE FROM Music_History WHERE song_file = :file")
    suspend fun deleteHistory(file: String)

    @Query("DELETE FROM Songs WHERE file = :file")
    suspend fun deleteSong(file: String)

    @Query("SELECT * FROM Songs WHERE downloaded_link IS NOT NULL AND downloaded_link != '' ORDER BY date_download DESC")
    suspend fun getDownloaded(): List<SongEntity>

    @Insert
    suspend fun recordDeletion(tombstone: SyncDeletionEntity)
}

@Dao
interface SyncDeletionDao {
    @Insert
    suspend fun insert(tombstone: SyncDeletionEntity)

    @Query("SELECT * FROM Sync_Deletions WHERE table_name IN (:tables)")
    suspend fun forTables(tables: List<String>): List<SyncDeletionEntity>

    @Query("SELECT * FROM Sync_Deletions")
    suspend fun all(): List<SyncDeletionEntity>

    @Query("DELETE FROM Sync_Deletions WHERE table_name IN (:tables)")
    suspend fun clearTables(tables: List<String>)
}

@Dao
interface PlaylistDao {
    // Mirrors desktop ON CONFLICT(id) DO UPDATE.
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(items: List<PlaylistEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertSongLinks(items: List<SongPlaylistEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertLyrics(items: List<LyricEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertMusicHistory(items: List<MusicHistoryEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertPlaylistHistory(items: List<PlaylistHistoryEntity>)

    // ---- Playlists UI (load/new/update/delete + links + history) ----
    @Query("SELECT * FROM Playlists ORDER BY title")
    suspend fun getPlaylists(): List<PlaylistEntity>

    @Query("SELECT * FROM Playlists WHERE id = :id LIMIT 1")
    suspend fun getPlaylist(id: Long): PlaylistEntity?

    @Insert
    suspend fun insertPlaylist(p: PlaylistEntity): Long

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertPlaylistIgnore(p: PlaylistEntity): Long

    @Update
    suspend fun updatePlaylist(p: PlaylistEntity)

    @Query("SELECT id FROM Playlists")
    suspend fun allPlaylistIds(): List<Long>

    @Query("DELETE FROM Playlists WHERE id = :id")
    suspend fun deletePlaylistById(id: Long)

    @Query("UPDATE Playlists SET title = :title, description = :description WHERE id = :id")
    suspend fun renamePlaylist(id: Long, title: String, description: String?)

    @Query("UPDATE Playlists SET thumbnail = :thumbnail WHERE id = :id")
    suspend fun setThumbnail(id: Long, thumbnail: String?)

    @Query("DELETE FROM Playlist_History WHERE playlist_id = :id")
    suspend fun deletePlaylistHistory(id: Long)

    @Query("DELETE FROM Song_Playlist WHERE playlist_id = :id")
    suspend fun deletePlaylistLinks(id: Long)

    @Query("DELETE FROM Playlists WHERE id = :id")
    suspend fun deletePlaylist(id: Long)

    @Query("SELECT * FROM Song_Playlist WHERE playlist_id = :playlistId ORDER BY date_added")
    suspend fun getLinks(playlistId: Long): List<SongPlaylistEntity>

    @Query("SELECT COUNT(*) FROM Song_Playlist WHERE playlist_id = :playlistId")
    suspend fun countLinks(playlistId: Long): Int

    @Query("SELECT playlist_id FROM Song_Playlist WHERE song_file = :file")
    suspend fun getIdsForSong(file: String): List<Long>

    @Query("DELETE FROM Song_Playlist WHERE song_file = :file")
    suspend fun deleteLinksForSong(file: String)

    @Insert
    suspend fun insertLink(link: SongPlaylistEntity)

    @Insert
    suspend fun insertMusicHistory(entry: MusicHistoryEntity)

    @Query("SELECT * FROM Music_History ORDER BY date_played DESC")
    suspend fun getMusicHistory(): List<MusicHistoryEntity>

    @Query("SELECT * FROM Lyrics WHERE song_file = :file LIMIT 1")
    suspend fun lyricFor(file: String): LyricEntity?

    @Insert
    suspend fun insertLyric(entry: LyricEntity): Long

    @Query("UPDATE Lyrics SET lyrics = :text WHERE id = :id")
    suspend fun updateLyric(id: Long, text: String)

    // ---- Sync support (RemoteSync / RemoteWriter) ----

    @Query("SELECT * FROM Song_Playlist")
    suspend fun allSongLinks(): List<SongPlaylistEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertSongLinkIgnore(link: SongPlaylistEntity): Long

    @Update
    suspend fun updateSongLink(link: SongPlaylistEntity)

    @Query("DELETE FROM Song_Playlist WHERE playlist_id IN (:playlistIds)")
    suspend fun deleteSongLinksForPlaylists(playlistIds: List<Long>)

    @Query("SELECT * FROM Lyrics WHERE id = :id LIMIT 1")
    suspend fun lyricById(id: Long): LyricEntity?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertLyricIgnore(entry: LyricEntity): Long

    @Update
    suspend fun updateLyricEntity(entry: LyricEntity)

    @Query("UPDATE Lyrics SET offset_ms = :ms WHERE song_file = :file")
    suspend fun setLyricOffset(file: String, ms: Int)

    @Query("DELETE FROM Lyrics WHERE song_file = :file")
    suspend fun deleteLyricsForSong(file: String)

    @Query("SELECT * FROM Lyrics")
    suspend fun allLyrics(): List<LyricEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertMusicHistoryIgnore(entry: MusicHistoryEntity): Long

    @Update
    suspend fun updateMusicHistory(entry: MusicHistoryEntity)

    @Query("SELECT COALESCE(MAX(id), 0) FROM Music_History")
    suspend fun maxMusicHistoryId(): Long

    @Query("SELECT * FROM Music_History ORDER BY id")
    suspend fun allMusicHistoryOrdered(): List<MusicHistoryEntity>

    @Query("DELETE FROM Music_History WHERE song_file = :file")
    suspend fun deleteMusicHistoryForSong(file: String)

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertPlaylistHistoryIgnore(entry: PlaylistHistoryEntity): Long

    @Update
    suspend fun updatePlaylistHistory(entry: PlaylistHistoryEntity)

    @Query("SELECT COALESCE(MAX(id), 0) FROM Playlist_History")
    suspend fun maxPlaylistHistoryId(): Long

    @Query("SELECT * FROM Playlist_History ORDER BY id")
    suspend fun allPlaylistHistoryOrdered(): List<PlaylistHistoryEntity>

}

@Dao
interface DownloadJobDao {
    @Insert
    suspend fun insert(job: DownloadJobEntity): Long

    @Query("SELECT * FROM Download_Queue ORDER BY id DESC LIMIT :limit")
    suspend fun recent(limit: Int): List<DownloadJobEntity>

    @Query("SELECT COUNT(*) FROM Download_Queue")
    suspend fun total(): Int

    @Query("SELECT * FROM Download_Queue WHERE status = 'queued' ORDER BY id ASC")
    suspend fun queued(): List<DownloadJobEntity>

    @Query("SELECT * FROM Download_Queue WHERE url = :url AND status IN ('queued', 'downloading') ORDER BY id ASC LIMIT 1")
    suspend fun activeByUrl(url: String): DownloadJobEntity?

    @Query("SELECT * FROM Download_Queue WHERE url = :url ORDER BY id DESC LIMIT 1")
    suspend fun latestByUrl(url: String): DownloadJobEntity?

    @Query("DELETE FROM Download_Queue WHERE status IN ('done', 'failed', 'cancelled')")
    suspend fun deleteTerminal(): Int

    @Query("SELECT COUNT(*) FROM Download_Queue WHERE status IN ('queued', 'downloading')")
    suspend fun countActive(): Int

    @Query("SELECT * FROM Download_Queue WHERE qid = :qid LIMIT 1")
    suspend fun byQid(qid: String): DownloadJobEntity?

    @Query("UPDATE Download_Queue SET status = :status, progress = :progress, error = :error, filename = :filename, updated_at = :updatedAt WHERE qid = :qid")
    suspend fun updateState(qid: String, status: String, progress: Float, error: String?, filename: String?, updatedAt: String)

    @Query("UPDATE Download_Queue SET status = 'queued', progress = 0, error = NULL, filename = NULL WHERE status IN ('queued', 'downloading')")
    suspend fun resetStuck(): Int
}

@Dao
interface PodcastDao {
    @Query("SELECT * FROM Podcasts ORDER BY date_download DESC")
    suspend fun getAll(): List<PodcastEntity>

    @Query("SELECT * FROM Podcasts WHERE file = :file LIMIT 1")
    suspend fun getByFile(file: String): PodcastEntity?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(p: PodcastEntity): Long

    @Update
    suspend fun update(p: PodcastEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(items: List<PodcastEntity>)

    @Query("UPDATE Podcasts SET title = :title, artist = :artist WHERE file = :file")
    suspend fun updateTitleArtist(file: String, title: String, artist: String)

    @Query("DELETE FROM Podcasts WHERE file = :file")
    suspend fun deleteByFile(file: String)
}

@Dao
interface PodcastTagDao {
    @Query("SELECT * FROM Podcast_Tags ORDER BY name")
    suspend fun getTags(): List<PodcastTagEntity>

    @Query("SELECT * FROM Podcast_Tags WHERE name = :name LIMIT 1")
    suspend fun getByName(name: String): PodcastTagEntity?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(tag: PodcastTagEntity): Long

    // Mirrors desktop ON CONFLICT(id) DO UPDATE (pull direction).
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(items: List<PodcastTagEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertLinks(items: List<PodcastTagLinkEntity>)

    @Query("UPDATE Podcast_Tags SET name = :name WHERE id = :id")
    suspend fun rename(id: Long, name: String)

    @Query("DELETE FROM Podcast_Tags WHERE id = :id")
    suspend fun deleteTag(id: Long)

    @Query("DELETE FROM Podcast_Tag_Links WHERE tag_id = :tagId")
    suspend fun deleteLinksForTag(tagId: Long)

    @Query("SELECT tag_id FROM Podcast_Tag_Links WHERE podcast_file = :file")
    suspend fun getTagIdsForFile(file: String): List<Long>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertLinkIgnore(link: PodcastTagLinkEntity): Long

    @Query("DELETE FROM Podcast_Tag_Links WHERE podcast_file = :file AND tag_id = :tagId")
    suspend fun deleteLink(file: String, tagId: Long)

    @Query("DELETE FROM Podcast_Tag_Links WHERE podcast_file = :file")
    suspend fun deleteLinksForFile(file: String)

    @Query("SELECT * FROM Podcast_Tag_Links")
    suspend fun getLinks(): List<PodcastTagLinkEntity>

    @Query("SELECT COUNT(*) FROM Podcast_Tag_Links WHERE tag_id = :tagId")
    suspend fun countLinks(tagId: Long): Int

    @Query("SELECT * FROM Podcast_Tags WHERE id = :id LIMIT 1")
    suspend fun tagById(id: Long): PodcastTagEntity?

    @Query("SELECT id FROM Podcast_Tags")
    suspend fun allTagIds(): List<Long>

    @Update
    suspend fun updateTag(tag: PodcastTagEntity)

    @Query("DELETE FROM Podcast_Tags WHERE id = :id")
    suspend fun deleteTagById(id: Long)

    @Update
    suspend fun updateLink(link: PodcastTagLinkEntity)

    @Query("DELETE FROM Podcast_Tag_Links WHERE tag_id IN (:tagIds)")
    suspend fun deleteLinksForTags(tagIds: List<Long>)
}

@Dao
interface PodcastTagLinkDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertLinkIgnore(link: PodcastTagLinkEntity): Long

    @Update
    suspend fun updateLink(link: PodcastTagLinkEntity)

    @Query("DELETE FROM Podcast_Tag_Links WHERE tag_id IN (:tagIds)")
    suspend fun deleteLinksForTags(tagIds: List<Long>)

    @Query("DELETE FROM Podcast_Tag_Links WHERE podcast_file = :file AND tag_id = :tagId")
    suspend fun deleteLink(file: String, tagId: Long)

    @Query("DELETE FROM Podcast_Tag_Links WHERE podcast_file = :file")
    suspend fun deleteLinksForFile(file: String)

    @Query("SELECT * FROM Podcast_Tag_Links")
    suspend fun getLinks(): List<PodcastTagLinkEntity>
}

@Dao
interface PodcastChapterDao {
    @Query("SELECT * FROM Podcast_Chapters WHERE id = :id LIMIT 1")
    suspend fun byId(id: Long): PodcastChapterEntity?

    @Query("SELECT * FROM Podcast_Chapters WHERE podcast_file = :file ORDER BY start_secs")
    suspend fun byEpisode(file: String): List<PodcastChapterEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(chapter: PodcastChapterEntity): Long

    @Update
    suspend fun update(chapter: PodcastChapterEntity)

    @Query("DELETE FROM Podcast_Chapters WHERE id = :id")
    suspend fun deleteById(id: Long): Int

    @Query("DELETE FROM Podcast_Chapters WHERE podcast_file = :file")
    suspend fun deleteForEpisode(file: String)

    @Query("SELECT * FROM Podcast_Chapters")
    suspend fun all(): List<PodcastChapterEntity>
}

@Dao
interface DailyMixDao {
    @Query("SELECT * FROM Daily_Mix WHERE mix_date = :date LIMIT 1")
    suspend fun getByDate(date: String): DailyMixEntity?

    @Query("SELECT * FROM Daily_Mix ORDER BY mix_date DESC")
    suspend fun getAll(): List<DailyMixEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun save(mix: DailyMixEntity)

    @Query("DELETE FROM Daily_Mix WHERE mix_date != :today")
    suspend fun purgeNonToday(today: String)

    @Query("DELETE FROM Daily_Mix WHERE mix_date < :newest")
    suspend fun deleteOlderThan(newest: String)
}

@Dao
interface SettingsDao {
    @Query("SELECT * FROM Settings WHERE id = 1 LIMIT 1")
    suspend fun get(): SettingsEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun save(settings: SettingsEntity)
}

@Database(
    entities = [
        SongEntity::class, PlaylistEntity::class, SongPlaylistEntity::class,
        LyricEntity::class, MusicHistoryEntity::class, PlaylistHistoryEntity::class,
        SyncDeletionEntity::class, SettingsEntity::class, DownloadJobEntity::class,
        PodcastEntity::class, DailyMixEntity::class,
        PodcastTagEntity::class, PodcastTagLinkEntity::class,
        PodcastChapterEntity::class
    ],
    version = 9,
    exportSchema = false
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun songDao(): SongDao
    abstract fun playlistDao(): PlaylistDao
    abstract fun settingsDao(): SettingsDao
    abstract fun downloadJobDao(): DownloadJobDao
    abstract fun podcastDao(): PodcastDao
    abstract fun dailyMixDao(): DailyMixDao
    abstract fun podcastTagDao(): PodcastTagDao
    abstract fun podcastTagLinkDao(): PodcastTagLinkDao
    abstract fun podcastChapterDao(): PodcastChapterDao
    abstract fun syncDeletionDao(): SyncDeletionDao

    companion object {
        @Volatile private var INSTANCE: AppDatabase? = null

        private val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Sync_Deletions (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "table_name TEXT NOT NULL, row_key TEXT NOT NULL, " +
                        "deleted_at TEXT)"
                )
            }
        }

        private val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Settings (" +
                        "id INTEGER PRIMARY KEY NOT NULL, " +
                        "current_volume REAL, limit_downloads INTEGER, songs_path TEXT, " +
                        "background_path TEXT, current_song TEXT, current_playlist INTEGER)"
                )
            }
        }

        private val MIGRATION_3_4 = object : Migration(3, 4) {            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Download_Queue (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "qid TEXT NOT NULL, url TEXT, title TEXT, artist TEXT, " +
                        "status TEXT NOT NULL DEFAULT 'queued', progress REAL NOT NULL DEFAULT 0, " +
                        "error TEXT, filename TEXT, created_at TEXT, updated_at TEXT)"
                )
                db.execSQL("CREATE UNIQUE INDEX IF NOT EXISTS index_Download_Queue_qid ON Download_Queue (qid)")
            }
        }

        private val MIGRATION_4_5 = object : Migration(4, 5) {
            override fun migrate(db: SupportSQLiteDatabase) {
                // Desktop Settings columns for queue persistence.
                db.execSQL("ALTER TABLE Settings ADD COLUMN queue_songs TEXT")
                db.execSQL("ALTER TABLE Settings ADD COLUMN custom_queue INTEGER")
            }
        }

        private val MIGRATION_5_6 = object : Migration(5, 6) {
            override fun migrate(db: SupportSQLiteDatabase) {
                // Second library dir (desktop Podcasts mirror).
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Podcasts (" +
                        "file TEXT PRIMARY KEY NOT NULL, downloaded_link TEXT, " +
                        "title TEXT NOT NULL, date_download TEXT, artist TEXT)"
                )
                // Per-day mix cache (desktop Daily_Mix mirror).
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Daily_Mix (" +
                        "mix_date TEXT PRIMARY KEY NOT NULL, song_files TEXT NOT NULL, " +
                        "created_at TEXT)"
                )
                // Playback context + podcasts folder (desktop Settings mirror).
                db.execSQL("ALTER TABLE Settings ADD COLUMN queue_source TEXT")
                db.execSQL("ALTER TABLE Settings ADD COLUMN podcasts_path TEXT")
                // Desktop Download_Queue.is_podcast (podcasts folder routing).
                try {
                    db.execSQL("ALTER TABLE Download_Queue ADD COLUMN is_podcast INTEGER NOT NULL DEFAULT 0")
                } catch (_: Exception) {
                }
            }
        }

        private val MIGRATION_6_7 = object : Migration(6, 7) {
            override fun migrate(db: SupportSQLiteDatabase) {
                // Desktop Settings crossfade prefs (local-only).
                db.execSQL("ALTER TABLE Settings ADD COLUMN crossfade_enabled INTEGER")
                db.execSQL("ALTER TABLE Settings ADD COLUMN crossfade_seconds REAL")
            }
        }

        private val MIGRATION_7_8 = object : Migration(7, 8) {
            override fun migrate(db: SupportSQLiteDatabase) {
                // Podcast tags (same shape as Playlists + Song_Playlist).
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Podcast_Tags (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "name TEXT NOT NULL)"
                )
                db.execSQL("CREATE UNIQUE INDEX IF NOT EXISTS index_Podcast_Tags_name ON Podcast_Tags (name)")
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Podcast_Tag_Links (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "podcast_file TEXT NOT NULL, tag_id INTEGER NOT NULL)"
                )
                db.execSQL(
                    "CREATE UNIQUE INDEX IF NOT EXISTS " +
                        "index_Podcast_Tag_Links_podcast_file_tag_id " +
                        "ON Podcast_Tag_Links (podcast_file, tag_id)"
                )
            }
        }

        private val MIGRATION_8_9 = object : Migration(8, 9) {
            override fun migrate(db: SupportSQLiteDatabase) {
                // Lyrics highlight offsets (desktop offset_ms / web ±20000ms).
                db.execSQL("ALTER TABLE Lyrics ADD COLUMN offset_ms INTEGER NOT NULL DEFAULT 0")
                // Episode chapter marks (desktop Podcast_Chapters mirror).
                db.execSQL(
                    "CREATE TABLE IF NOT EXISTS Podcast_Chapters (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, " +
                        "podcast_file TEXT NOT NULL, name TEXT NOT NULL, " +
                        "start_secs REAL NOT NULL, end_secs REAL)"
                )
                db.execSQL(
                    "CREATE INDEX IF NOT EXISTS index_Podcast_Chapters_podcast_file_start_secs " +
                        "ON Podcast_Chapters (podcast_file, start_secs)"
                )
            }
        }

        fun get(context: Context): AppDatabase =
            INSTANCE ?: synchronized(this) {
                INSTANCE ?: Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "music_player.db" // same filename as desktop
                ).addMigrations(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4, MIGRATION_4_5, MIGRATION_5_6, MIGRATION_6_7, MIGRATION_7_8, MIGRATION_8_9).build().also { INSTANCE = it }
            }
    }
}
