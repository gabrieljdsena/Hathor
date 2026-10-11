package com.musicplayer.android.data.db

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update

// Sync data access: upsert-by-key, scoped link replace, tombstones,
// MAX(id) history reads. Mirrors the desktop SQL in
// apps/desktop/services/database.py + apps/desktop/sync.py.

@Dao
interface SongDao {
    @Query("SELECT * FROM songs WHERE file = :file LIMIT 1")
    suspend fun byFile(file: String): SongEntity?

    @Query("SELECT * FROM songs ORDER BY file COLLATE NOCASE")
    suspend fun all(): List<SongEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(song: SongEntity): Long

    @Update
    suspend fun update(song: SongEntity)

    @Query("DELETE FROM songs WHERE file = :file")
    suspend fun deleteByFile(file: String)
}

@Dao
interface PodcastDao {
    @Query("SELECT * FROM podcasts WHERE file = :file LIMIT 1")
    suspend fun byFile(file: String): PodcastEntity?

    @Query("SELECT * FROM podcasts ORDER BY file COLLATE NOCASE")
    suspend fun all(): List<PodcastEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(episode: PodcastEntity): Long

    @Update
    suspend fun update(episode: PodcastEntity)

    @Query("DELETE FROM podcasts WHERE file = :file")
    suspend fun deleteByFile(file: String)
}

@Dao
interface PlaylistDao {
    @Query("SELECT * FROM playlists WHERE id = :id LIMIT 1")
    suspend fun byId(id: Long): PlaylistEntity?

    @Query("SELECT * FROM playlists ORDER BY title COLLATE NOCASE")
    suspend fun all(): List<PlaylistEntity>

    // Explicit-id upsert (remote ids are a shared global namespace):
    // insert-or-ignore, then update the existing row on conflict.
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(playlist: PlaylistEntity): Long

    @Update
    suspend fun update(playlist: PlaylistEntity)

    @Query("SELECT id FROM playlists")
    suspend fun allIds(): List<Long>

    @Query("DELETE FROM playlists WHERE id = :id")
    suspend fun deleteById(id: Long)
}

@Dao
interface SongPlaylistDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(link: SongPlaylistLink): Long

    @Update
    suspend fun update(link: SongPlaylistLink)

    @Query("DELETE FROM song_playlist WHERE playlist_id IN (:playlistIds)")
    suspend fun deleteForPlaylists(playlistIds: List<Long>)

    @Query("DELETE FROM song_playlist WHERE song_file = :file")
    suspend fun deleteByFile(file: String)

    @Query("SELECT * FROM song_playlist")
    suspend fun all(): List<SongPlaylistLink>
}

@Dao
interface PodcastTagDao {
    @Query("SELECT * FROM podcast_tags WHERE id = :id LIMIT 1")
    suspend fun byId(id: Long): PodcastTagEntity?

    @Query("SELECT * FROM podcast_tags ORDER BY name COLLATE NOCASE")
    suspend fun all(): List<PodcastTagEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(tag: PodcastTagEntity): Long

    @Update
    suspend fun update(tag: PodcastTagEntity)

    @Query("SELECT id FROM podcast_tags")
    suspend fun allIds(): List<Long>

    @Query("DELETE FROM podcast_tags WHERE id = :id")
    suspend fun deleteById(id: Long)
}

@Dao
interface PodcastTagLinkDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(link: PodcastTagLink): Long

    @Update
    suspend fun update(link: PodcastTagLink)

    @Query("DELETE FROM podcast_tag_links WHERE tag_id IN (:tagIds)")
    suspend fun deleteForTags(tagIds: List<Long>)

    @Query("DELETE FROM podcast_tag_links WHERE podcast_file = :file AND tag_id = :tagId")
    suspend fun deleteLink(file: String, tagId: Long)

    @Query("DELETE FROM podcast_tag_links WHERE podcast_file = :file")
    suspend fun deleteForEpisode(file: String)

    @Query("SELECT * FROM podcast_tag_links")
    suspend fun all(): List<PodcastTagLink>
}

@Dao
interface LyricDao {
    @Query("SELECT * FROM lyrics WHERE id = :id LIMIT 1")
    suspend fun byId(id: Long): LyricEntity?

    @Query("SELECT * FROM lyrics WHERE song_file = :file LIMIT 1")
    suspend fun bySongFile(file: String): LyricEntity?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(lyric: LyricEntity): Long

    @Update
    suspend fun update(lyric: LyricEntity)

    @Query("UPDATE lyrics SET offset_ms = :ms WHERE song_file = :file")
    suspend fun setOffsetMs(file: String, ms: Int)

    @Query("DELETE FROM lyrics WHERE song_file = :file")
    suspend fun deleteByFile(file: String)

    @Query("SELECT * FROM lyrics")
    suspend fun all(): List<LyricEntity>
}

@Dao
interface PodcastChapterDao {
    @Query("SELECT * FROM podcast_chapters WHERE id = :id LIMIT 1")
    suspend fun byId(id: Long): PodcastChapterEntity?

    @Query("SELECT * FROM podcast_chapters WHERE podcast_file = :file ORDER BY start_secs")
    suspend fun byEpisode(file: String): List<PodcastChapterEntity>

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(chapter: PodcastChapterEntity): Long

    @Update
    suspend fun update(chapter: PodcastChapterEntity)

    @Query("DELETE FROM podcast_chapters WHERE id = :id")
    suspend fun deleteById(id: Long): Int

    @Query("DELETE FROM podcast_chapters WHERE podcast_file = :file")
    suspend fun deleteForEpisode(file: String)

    @Query("SELECT * FROM podcast_chapters")
    suspend fun all(): List<PodcastChapterEntity>
}

@Dao
interface MusicHistoryDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(entry: MusicHistoryEntry): Long

    @Update
    suspend fun update(entry: MusicHistoryEntry)

    @Query("SELECT COALESCE(MAX(id), 0) FROM music_history")
    suspend fun maxId(): Long

    @Query("SELECT * FROM music_history ORDER BY id")
    suspend fun allOrdered(): List<MusicHistoryEntry>

    @Query("DELETE FROM music_history WHERE song_file = :file")
    suspend fun deleteByFile(file: String)
}

@Dao
interface PlaylistHistoryDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(entry: PlaylistHistoryEntry): Long

    @Update
    suspend fun update(entry: PlaylistHistoryEntry)

    @Query("SELECT COALESCE(MAX(id), 0) FROM playlist_history")
    suspend fun maxId(): Long

    @Query("SELECT * FROM playlist_history ORDER BY id")
    suspend fun allOrdered(): List<PlaylistHistoryEntry>

    @Query("DELETE FROM playlist_history WHERE playlist_id = :playlistId")
    suspend fun deleteByPlaylistId(playlistId: Long)
}

@Dao
interface DailyMixDao {
    @Query("SELECT * FROM daily_mix WHERE mix_date = :date LIMIT 1")
    suspend fun byDate(date: String): DailyMixEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun replace(mix: DailyMixEntity)

    @Query("SELECT * FROM daily_mix")
    suspend fun all(): List<DailyMixEntity>

    @Query("SELECT COALESCE(MAX(mix_date), '') FROM daily_mix")
    suspend fun newestDate(): String

    @Query("DELETE FROM daily_mix WHERE mix_date < :today")
    suspend fun pruneBefore(today: String)
}

@Dao
interface SyncDeletionDao {
    @Insert
    suspend fun insert(deletion: SyncDeletion)

    @Query("SELECT * FROM sync_deletions WHERE table_name IN (:tables)")
    suspend fun forTables(tables: List<String>): List<SyncDeletion>

    @Query("SELECT * FROM sync_deletions")
    suspend fun all(): List<SyncDeletion>

    @Query("DELETE FROM sync_deletions WHERE table_name IN (:tables)")
    suspend fun clearTables(tables: List<String>)
}
