package com.musicplayer.android.data.remote

import android.util.Log
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncApiException
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.engine.DownloadEngine
import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import org.json.JSONArray
import org.json.JSONObject

// Push-side writes to the Hathor Web API (replaces the retired remote
// MySQL/TiDB leg — no schema init, no AUTO_INCREMENT alignment, no JDBC).
// Local rows + Sync_Deletions tombstones go up via POST import (the server
// applies scoped replaces and idempotent history inserts); files the server
// lacks come back as missingFiles and are uploaded as raw bytes via PUT.
// Tombstones clear only after the server accepts them, so a failed push
// retries everything idempotently next time.
//
// Reads data.local (music_player.db) — the same Room database the library
// screens use — so what the user sees is exactly what gets pushed. It used
// to read data.db/hathor.db, a second instance the UI never displayed.
object RemoteWriter {
    private const val TAG = "RemoteWriter"

    private val songTables = listOf(
        "songs", "playlists", "lyrics", "music_history", "playlist_history",
    )
    private val podcastTables = listOf("podcasts", "podcast_tags", "podcast_chapters")

    data class PushReport(val rows: Int, val message: String)

    suspend fun pushSongs(db: AppDatabase, engine: DownloadEngine, api: SyncApi): PushReport {
        val payload = JSONObject()
        var rows = 0
        payload.put("Songs", JSONArray(db.songDao().getAll().map {
            fileRowJson(it.file, it.downloadedLink, it.title, it.dateDownload, it.artist)
        }.also { rows += it.size }))
        payload.put("Playlists", JSONArray(db.playlistDao().getPlaylists().map {
            JSONObject().put("Id", it.id).put("Title", it.title)
                .put("Description", it.description).put("Thumbnail", it.thumbnail)
        }.also { rows += it.size }))
        payload.put("SongLinks", JSONArray(db.playlistDao().allSongLinks().map {
            JSONObject().put("Id", it.id).put("SongFile", it.songFile)
                .put("PlaylistId", it.playlistId)
                .put("DateAddedUtc", isoDate(it.dateAdded))
        }.also { rows += it.size }))
        payload.put("Lyrics", JSONArray(db.playlistDao().allLyrics().map {
            JSONObject().put("Id", it.id).put("SongFile", it.songFile)
                .put("LyricsJson", it.lyrics).put("OffsetMs", it.offsetMs)
        }.also { rows += it.size }))
        payload.put("MusicHistory", JSONArray(db.playlistDao().allMusicHistoryOrdered().map {
            JSONObject().put("Id", it.id).put("SongFile", it.songFile)
                .put("DatePlayedUtc", isoDate(it.datePlayed))
        }.also { rows += it.size }))
        payload.put("PlaylistHistory", JSONArray(db.playlistDao().allPlaylistHistoryOrdered().map {
            JSONObject().put("Id", it.id).put("PlaylistId", it.playlistId)
                .put("DatePlayedUtc", isoDate(it.datePlayed))
        }.also { rows += it.size }))
        payload.put("DailyMix", JSONArray(db.dailyMixDao().getAll().map {
            JSONObject().put("MixDate", it.mixDate).put("SongFilesJson", it.songFiles)
        }.also { rows += it.size }))
        payload.put("Deletions", deletionsJson(db, songTables))

        val result = try {
            api.postImport(payload)
        } catch (e: SyncApiException) {
            throw IllegalStateException(e.message)
        }
        val uploaded = uploadMissing(api, engine.outputDir(), "songs", result)
        db.syncDeletionDao().clearTables(songTables)
        return PushReport(rows, "Pushed $rows rows to sync server." +
            (if (uploaded > 0) " Uploaded $uploaded file(s)." else ""))
    }

    suspend fun pushPodcasts(db: AppDatabase, engine: DownloadEngine, api: SyncApi): PushReport {
        val payload = JSONObject()
        var rows = 0
        payload.put("Podcasts", JSONArray(db.podcastDao().getAll().map {
            fileRowJson(it.file, it.downloadedLink, it.title, it.dateDownload, it.artist)
        }.also { rows += it.size }))
        payload.put("PodcastTags", JSONArray(db.podcastTagDao().getTags().map {
            JSONObject().put("Id", it.id).put("Name", it.name)
        }.also { rows += it.size }))
        payload.put("PodcastTagLinks", JSONArray(db.podcastTagLinkDao().getLinks().map {
            JSONObject().put("Id", it.id).put("PodcastFile", it.podcastFile).put("TagId", it.tagId)
        }.also { rows += it.size }))
        payload.put("PodcastChapters", JSONArray(db.podcastChapterDao().all().map {
            JSONObject().put("Id", it.id).put("PodcastFile", it.podcastFile)
                .put("Name", it.name).put("StartSecs", it.startSecs).put("EndSecs", it.endSecs)
        }.also { rows += it.size }))
        payload.put("Deletions", deletionsJson(db, podcastTables))

        val result = try {
            api.postImport(payload)
        } catch (e: SyncApiException) {
            throw IllegalStateException(e.message)
        }
        val uploaded = uploadMissing(api, engine.podcastsDir(), "podcasts", result)
        db.syncDeletionDao().clearTables(podcastTables)
        return PushReport(rows, "Pushed $rows rows to sync server." +
            (if (uploaded > 0) " Uploaded $uploaded file(s)." else ""))
    }

    private fun fileRowJson(
        file: String, downloadedLink: String?, title: String, localDate: String?, artist: String?,
    ): JSONObject = JSONObject()
        .put("File", file)
        .put("DownloadedLink", downloadedLink)
        .put("Title", title)
        .put("DateDownloadUtc", isoDate(localDate))
        .put("Artist", artist)

    private suspend fun deletionsJson(db: AppDatabase, tables: List<String>): JSONArray =
        JSONArray(db.syncDeletionDao().forTables(tables).map {
            JSONObject().put("TableName", it.tableName).put("RowKey", it.rowKey)
        })

    // Raw bytes for files the server catalog lacks (import's missingFiles).
    // A missing local file is skipped (the next push simply reports it
    // missing again until the bytes exist locally, same as desktop).
    private fun uploadMissing(
        api: SyncApi, dir: File, library: String, result: JSONObject,
    ): Int {
        var uploaded = 0
        for (file in SyncApi.missingFiles(result)) {
            if ('/' in file || '\\' in file) continue
            val src = dir.resolve(file)
            if (!src.exists()) {
                Log.w(TAG, "Push upload: local file missing, skipping $file")
                continue
            }
            try {
                api.putFile(file, library, src)
                uploaded++
            } catch (e: SyncApiException) {
                throw IllegalStateException(e.message)
            }
        }
        return uploaded
    }

    // Local rows keep desktop text dates ("yyyy-MM-dd" for date_download,
    // "yyyy-MM-dd HH:mm:ss" for links/history, UTC). The server DTOs take a
    // non-nullable DateTime, so always emit ISO-8601 UTC and fall back to
    // "now" for blank/unparseable values instead of failing the import.
    private fun isoDate(raw: String?): String {
        val s = raw?.trim().orEmpty()
        if (s.isBlank()) return Instant.now().toString()
        return try {
            if (s.length >= 19) {
                val normalized = s.replace(' ', 'T')
                val zoned = normalized.endsWith("Z") || '+' in normalized.substring(10) ||
                    '-' in normalized.substring(11)
                Instant.parse(if (zoned) normalized else normalized + "Z")
            } else {
                LocalDate.parse(s).atStartOfDay().toInstant(ZoneOffset.UTC)
            }.toString()
        } catch (_: Exception) {
            Instant.now().toString()
        }
    }
}
