package com.musicplayer.android.data.remote

import android.util.Log
import com.musicplayer.android.data.api.SyncApi
import com.musicplayer.android.data.api.SyncApiException
import com.musicplayer.android.data.db.AppDatabase
import com.musicplayer.android.engine.DownloadEngine
import java.io.File
import org.json.JSONArray
import org.json.JSONObject

// Push-side writes to the Hathor Web API (replaces the retired remote
// MySQL/TiDB leg — no schema init, no AUTO_INCREMENT alignment, no JDBC).
// Local rows + Sync_Deletions tombstones go up via POST import (the server
// applies scoped replaces and idempotent history inserts); files the server
// lacks come back as missingFiles and are uploaded as raw bytes via PUT.
// Tombstones clear only after the server accepts them, so a failed push
// retries everything idempotently next time.
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
        payload.put("Songs", JSONArray(db.songDao().all().map { fileRowJson(it.file, it.downloadedLink, it.title, it.dateDownloadMillis, it.artist) }.also { rows += it.size() }))
        val playlists = db.playlistDao().all()
        payload.put("Playlists", JSONArray(playlists.map {
            JSONObject().put("Id", it.id).put("Title", it.title)
                .put("Description", it.description).put("Thumbnail", it.thumbnail)
        }.also { rows += it.size() }))
        payload.put("SongLinks", JSONArray(db.songPlaylistDao().all().map {
            JSONObject().put("Id", it.id).put("SongFile", it.songFile)
                .put("PlaylistId", it.playlistId).put("DateAddedUtc", SyncApi.isoNow(it.dateAddedMillis))
        }.also { rows += it.size() }))
        payload.put("Lyrics", JSONArray(db.lyricDao().all().map {
            JSONObject().put("Id", it.id).put("SongFile", it.songFile)
                .put("LyricsJson", it.lyrics).put("OffsetMs", it.offsetMs)
        }.also { rows += it.size() }))
        payload.put("MusicHistory", JSONArray(db.musicHistoryDao().allOrdered().map {
            JSONObject().put("Id", it.id).put("SongFile", it.songFile)
                .put("DatePlayedUtc", SyncApi.isoNow(it.datePlayedMillis))
        }.also { rows += it.size() }))
        payload.put("PlaylistHistory", JSONArray(db.playlistHistoryDao().allOrdered().map {
            JSONObject().put("Id", it.id).put("PlaylistId", it.playlistId)
                .put("DatePlayedUtc", SyncApi.isoNow(it.datePlayedMillis))
        }.also { rows += it.size() }))
        val mixes = db.dailyMixDao().all()
        payload.put("DailyMix", JSONArray(mixes.map {
            JSONObject().put("MixDate", it.mixDate).put("SongFilesJson", it.songFilesJson)
        }.also { rows += it.size() }))
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
        payload.put("Podcasts", JSONArray(db.podcastDao().all().map {
            fileRowJson(it.file, it.downloadedLink, it.title, it.dateDownloadMillis, it.artist)
        }.also { rows += it.size() }))
        payload.put("PodcastTags", JSONArray(db.podcastTagDao().all().map {
            JSONObject().put("Id", it.id).put("Name", it.name)
        }.also { rows += it.size() }))
        payload.put("PodcastTagLinks", JSONArray(db.podcastTagLinkDao().all().map {
            JSONObject().put("Id", it.id).put("PodcastFile", it.podcastFile).put("TagId", it.tagId)
        }.also { rows += it.size() }))
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
        file: String, downloadedLink: String?, title: String, dateMillis: Long, artist: String?,
    ): JSONObject = JSONObject()
        .put("File", file)
        .put("DownloadedLink", downloadedLink)
        .put("Title", title)
        .put("DateDownloadUtc", SyncApi.isoNow(dateMillis))
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
                api.putFile(file, library, src.readBytes())
                uploaded++
            } catch (e: SyncApiException) {
                throw IllegalStateException(e.message)
            }
        }
        return uploaded
    }
}
