package com.musicplayer.android.data.db

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

// Local Room tables. Table AND column names are byte-identical to the
// desktop SQLite tables (apps/desktop/database.sql) and the shared remote
// MySQL/TiDB tables (apps/desktop/sync.py REMOTE_SCHEMA, lowercase):
// pull upserts and push upserts map 1:1 with no name translation.
// Dates are epoch millis (Long); ISO/text conversion happens at the
// remote boundary only. Single-user phone: no UserId column (the remote
// is one global namespace, last-writer-wins, exactly like desktop).

@Entity(tableName = "songs")
data class SongEntity(
    @PrimaryKey @ColumnInfo(name = "file") val file: String,
    @ColumnInfo(name = "downloaded_link") val downloadedLink: String?,
    @ColumnInfo(name = "title") val title: String,
    @ColumnInfo(name = "date_download") val dateDownloadMillis: Long,
    @ColumnInfo(name = "artist") val artist: String?,
)

@Entity(tableName = "podcasts")
data class PodcastEntity(
    @PrimaryKey @ColumnInfo(name = "file") val file: String,
    @ColumnInfo(name = "downloaded_link") val downloadedLink: String?,
    @ColumnInfo(name = "title") val title: String,
    @ColumnInfo(name = "date_download") val dateDownloadMillis: Long,
    @ColumnInfo(name = "artist") val artist: String?,
)

@Entity(tableName = "playlists")
data class PlaylistEntity(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "title") val title: String,
    @ColumnInfo(name = "description") val description: String?,
    @ColumnInfo(name = "thumbnail") val thumbnail: String?,
)

@Entity(tableName = "song_playlist")
data class SongPlaylistLink(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "song_file") val songFile: String,
    @ColumnInfo(name = "playlist_id") val playlistId: Long,
    @ColumnInfo(name = "date_added") val dateAddedMillis: Long,
)

@Entity(
    tableName = "podcast_tags",
    indices = [Index(value = ["name"], unique = true)],
)
data class PodcastTagEntity(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "name") val name: String,
)

@Entity(
    tableName = "podcast_tag_links",
    indices = [Index(value = ["podcast_file", "tag_id"], unique = true)],
)
data class PodcastTagLink(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "podcast_file") val podcastFile: String,
    @ColumnInfo(name = "tag_id") val tagId: Long,
)

@Entity(tableName = "lyrics")
data class LyricEntity(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "song_file") val songFile: String,
    @ColumnInfo(name = "lyrics") val lyrics: String?,
    @ColumnInfo(name = "offset_ms") val offsetMs: Int = 0,
)

@Entity(tableName = "music_history")
data class MusicHistoryEntry(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "song_file") val songFile: String,
    @ColumnInfo(name = "date_played") val datePlayedMillis: Long,
)

@Entity(tableName = "playlist_history")
data class PlaylistHistoryEntry(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "playlist_id") val playlistId: Long,
    @ColumnInfo(name = "date_played") val datePlayedMillis: Long,
)

@Entity(tableName = "daily_mix")
data class DailyMixEntity(
    @PrimaryKey @ColumnInfo(name = "mix_date") val mixDate: String,
    @ColumnInfo(name = "song_files") val songFilesJson: String,
)

@Entity(tableName = "sync_deletions")
data class SyncDeletion(
    @PrimaryKey(autoGenerate = true) @ColumnInfo(name = "id") val id: Long = 0,
    @ColumnInfo(name = "table_name") val tableName: String,
    @ColumnInfo(name = "row_key") val rowKey: String,
)
