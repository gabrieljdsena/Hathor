package com.musicplayer.android.data.remote

import java.sql.Connection
import java.sql.ResultSet

// Pull-side reads from the shared remote (desktop
// sync_remote_to_local_and_download + web RemotePullService, 1:1):
// songs + podcasts + playlists + links + lyrics + both histories +
// tags + mix, with guarded tables for old remotes. Throws on connect
// failure (callers shape the message); missing tables yield empty lists.

data class RemoteSongRow(
    val file: String,
    val downloadedLink: String?,
    val title: String,
    val dateDownloadMillis: Long,
    val artist: String?,
)

data class RemotePlaylistRow(
    val id: Long,
    val title: String,
    val description: String?,
    val thumbnail: String?,
)

data class RemoteSongLinkRow(
    val id: Long,
    val songFile: String,
    val playlistId: Long,
    val dateAddedMillis: Long,
)

data class RemoteLyricRow(
    val id: Long,
    val songFile: String,
    val lyrics: String?,
    val offsetMs: Int,
)

data class RemoteMusicHistoryRow(
    val id: Long,
    val songFile: String,
    val datePlayedMillis: Long,
)

data class RemotePlaylistHistoryRow(
    val id: Long,
    val playlistId: Long,
    val datePlayedMillis: Long,
)

data class RemoteTagRow(val id: Long, val name: String)

data class RemoteTagLinkRow(val id: Long, val podcastFile: String, val tagId: Long)

data class RemoteMixRow(val mixDate: String, val songFilesJson: String)

data class RemoteSnapshot(
    val songs: List<RemoteSongRow>,
    val podcasts: List<RemoteSongRow>,
    val playlists: List<RemotePlaylistRow>,
    val songLinks: List<RemoteSongLinkRow>,
    val lyrics: List<RemoteLyricRow>,
    val musicHistory: List<RemoteMusicHistoryRow>,
    val playlistHistory: List<RemotePlaylistHistoryRow>,
    val tags: List<RemoteTagRow>,
    val tagLinks: List<RemoteTagLinkRow>,
    val mixes: List<RemoteMixRow>,
)

object RemoteReader {
    // Epoch millis; TiDB TIMESTAMP comes back naive — treat as UTC (web UtcDate).
    private fun ResultSet.millis(ix: Int): Long {
        val ts = getTimestamp(ix) ?: return System.currentTimeMillis()
        return ts.time
    }

    private fun ResultSet.optText(ix: Int): String? {
        val v = getString(ix)
        return if (wasNull()) null else v
    }

    fun readAll(conn: Connection): RemoteSnapshot {
        if (!RemoteDb.tableExists(conn, "songs")) {
            throw IllegalStateException("Remote database not initialized yet.")
        }
        val songs = conn.prepareStatement(
            "SELECT file, downloaded_link, title, date_download, artist FROM songs",
        ).use { stmt ->
            stmt.executeQuery().use { rs ->
                buildList {
                    while (rs.next()) {
                        add(
                            RemoteSongRow(
                                rs.getString(1), rs.optText(2), rs.getString(3),
                                rs.millis(4), rs.optText(5),
                            ),
                        )
                    }
                }
            }
        }
        val podcasts = if (RemoteDb.tableExists(conn, "podcasts")) {
            conn.prepareStatement(
                "SELECT file, downloaded_link, title, date_download, artist FROM podcasts",
            ).use { stmt ->
                stmt.executeQuery().use { rs ->
                    buildList {
                        while (rs.next()) {
                            add(
                                RemoteSongRow(
                                    rs.getString(1), rs.optText(2), rs.getString(3),
                                    rs.millis(4), rs.optText(5),
                                ),
                            )
                        }
                    }
                }
            }
        } else {
            emptyList()
        }
        val playlists = conn.prepareStatement(
            "SELECT id, title, description, thumbnail FROM playlists",
        ).use { stmt ->
            stmt.executeQuery().use { rs ->
                buildList {
                    while (rs.next()) {
                        add(
                            RemotePlaylistRow(
                                rs.getLong(1), rs.getString(2),
                                rs.optText(3), rs.optText(4),
                            ),
                        )
                    }
                }
            }
        }
        val songLinks = conn.prepareStatement(
            "SELECT id, song_file, playlist_id, date_added FROM song_playlist",
        ).use { stmt ->
            stmt.executeQuery().use { rs ->
                buildList {
                    while (rs.next()) {
                        add(
                            RemoteSongLinkRow(
                                rs.getLong(1), rs.getString(2),
                                rs.getLong(3), rs.millis(4),
                            ),
                        )
                    }
                }
            }
        }
        // offset_ms postdates old remotes (web ColumnExistsAsync parity).
        val lyricsSql = if (RemoteDb.columnExists(conn, "lyrics", "offset_ms")) {
            "SELECT id, song_file, lyrics, offset_ms FROM lyrics"
        } else {
            "SELECT id, song_file, lyrics FROM lyrics"
        }
        val lyrics = conn.prepareStatement(lyricsSql).use { stmt ->
            stmt.executeQuery().use { rs ->
                val hasOffset = rs.metaData.columnCount > 3
                buildList {
                    while (rs.next()) {
                        add(
                            RemoteLyricRow(
                                rs.getLong(1), rs.getString(2), rs.optText(3),
                                if (hasOffset) rs.getInt(4) else 0,
                            ),
                        )
                    }
                }
            }
        }
        val musicHistory = conn.prepareStatement(
            "SELECT id, song_file, date_played FROM music_history",
        ).use { stmt ->
            stmt.executeQuery().use { rs ->
                buildList {
                    while (rs.next()) {
                        add(RemoteMusicHistoryRow(rs.getLong(1), rs.getString(2), rs.millis(3)))
                    }
                }
            }
        }
        val playlistHistory = conn.prepareStatement(
            "SELECT id, playlist_id, date_played FROM playlist_history",
        ).use { stmt ->
            stmt.executeQuery().use { rs ->
                buildList {
                    while (rs.next()) {
                        add(RemotePlaylistHistoryRow(rs.getLong(1), rs.getLong(2), rs.millis(3)))
                    }
                }
            }
        }
        val tags = if (RemoteDb.tableExists(conn, "podcast_tags")) {
            conn.prepareStatement("SELECT id, name FROM podcast_tags").use { stmt ->
                stmt.executeQuery().use { rs ->
                    buildList {
                        while (rs.next()) add(RemoteTagRow(rs.getLong(1), rs.getString(2)))
                    }
                }
            }
        } else {
            emptyList()
        }
        val tagLinks = if (RemoteDb.tableExists(conn, "podcast_tag_links")) {
            conn.prepareStatement(
                "SELECT id, podcast_file, tag_id FROM podcast_tag_links",
            ).use { stmt ->
                stmt.executeQuery().use { rs ->
                    buildList {
                        while (rs.next()) {
                            add(RemoteTagLinkRow(rs.getLong(1), rs.getString(2), rs.getLong(3)))
                        }
                    }
                }
            }
        } else {
            emptyList()
        }
        val mixes = if (RemoteDb.tableExists(conn, "daily_mix")) {
            conn.prepareStatement("SELECT mix_date, song_files FROM daily_mix").use { stmt ->
                stmt.executeQuery().use { rs ->
                    buildList {
                        while (rs.next()) add(RemoteMixRow(rs.getString(1), rs.getString(2)))
                    }
                }
            }
        } else {
            emptyList()
        }
        return RemoteSnapshot(
            songs, podcasts, playlists, songLinks, lyrics,
            musicHistory, playlistHistory, tags, tagLinks, mixes,
        )
    }
}
