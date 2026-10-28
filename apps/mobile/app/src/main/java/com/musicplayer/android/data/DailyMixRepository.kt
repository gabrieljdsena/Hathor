package com.musicplayer.android.data

import android.content.Context
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.DailyMixEntity
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import kotlin.random.Random

/**
 * Port of desktop DatabaseManager daily-mix + home-strip queries
 * (services/database.py: _get_ranked_files, _build_daily_mix_files,
 * get_daily_mix, get_recently_played, get_recently_downloaded,
 * get_library_count).
 *
 * Song objects carry at minimum {File, Title, Artist, Album, Duration,
 * CoverArt?, DateDownload?} — here [SongMeta] (hasCover instead of bytes;
 * covers resolve lazily via [MetadataRepository.coverArt]).
 */
data class DailyMix(
    val date: String,
    val songs: List<SongMeta>,
    val cached: Boolean
)

class DailyMixRepository(
    context: Context,
    private val engine: DownloadEngine,
    private val metadata: MetadataRepository
) {
    private val db = AppDatabase.get(context.applicationContext)

    private fun today(): String = DATE_FMT.format(Date())

    fun musicDir(): File = engine.outputDir()

    /** Live library count (mp3s on disk, like get_library_count). */
    suspend fun libraryCount(): Int = withContext(Dispatchers.IO) {
        val dir = musicDir()
        if (!dir.exists()) return@withContext 0
        dir.listFiles { f -> f.isFile && f.extension.equals("mp3", ignoreCase = true) }?.size ?: 0
    }

    /** All on-disk mp3 file names, sorted (stable base for generation). */
    private fun allFiles(): List<String> {
        val dir = musicDir()
        if (!dir.exists()) return emptyList()
        return dir.listFiles { f -> f.isFile && f.extension.equals("mp3", ignoreCase = true) }
            ?.map { it.name }?.sorted() ?: emptyList()
    }

    /** Files ordered by total play count (Music_History), most played first. */
    private fun rankedFiles(existing: Set<String>): List<String> {
        val out = mutableListOf<String>()
        try {
            db.openHelper.readableDatabase.query(
                "SELECT song_file, COUNT(*) as cnt, MAX(date_played) as last_played " +
                    "FROM Music_History GROUP BY song_file ORDER BY cnt DESC, last_played DESC"
            ).use { c ->
                while (c.moveToNext()) {
                    val f = c.getString(0)
                    if (f != null && f in existing) out.add(f)
                }
            }
        } catch (_: Exception) {
        }
        return out
    }

    /**
     * 2 from top 10, 5 from 11-25, 13 from 26-70, rest prefer OUTSIDE top 70
     * (discovery/long-tail), total 50. Small libraries: target =
     * min(50, library_size); tiers use min(quota, available); leftovers
     * (including top-tier leftovers) fill as fallback. Final list shuffled.
     */
    private fun buildMixFiles(allFiles: List<String>, ranked: List<String>): List<String> {
        val target = minOf(50, allFiles.size)
        if (target == 0) return emptyList()
        val allSet = allFiles.toSet()
        val top10 = ranked.take(10).filter { it in allSet }
        val top11_25 = ranked.drop(10).take(15).filter { it in allSet }
        val top26_70 = ranked.drop(25).take(45).filter { it in allSet }
        val top70set = ranked.take(70).toSet() intersect allSet

        val picked = mutableListOf<String>()
        for ((pool, n) in listOf(top10 to 2, top11_25 to 5, top26_70 to 13)) {
            if (pool.isEmpty()) continue
            if (picked.size >= target) break
            picked += pool.shuffled(Random.Default).take(minOf(n, pool.size, target - picked.size))
        }
        var remaining = target - picked.size
        if (remaining > 0) {
            val primary = allFiles.filter { it !in picked && it !in top70set }
            val take = minOf(remaining, primary.size)
            if (take > 0) {
                picked += primary.shuffled(Random.Default).take(take)
                remaining -= take
            }
            if (remaining > 0) {
                val pool = allFiles.filter { it !in picked }
                picked += pool.shuffled(Random.Default).take(minOf(remaining, pool.size))
            }
        }
        return picked.shuffled(Random.Default)
    }

    private suspend fun resolve(files: List<String>): List<SongMeta> {
        val dir = musicDir()
        // Date lookup mirrors get_all_songs (DB date wins).
        val dates = try {
            db.songDao().getAll().associate { it.file to it.dateDownload }
        } catch (_: Exception) {
            emptyMap()
        }
        val out = mutableListOf<SongMeta>()
        for (name in files) {
            val f = File(dir, name)
            if (!f.exists()) continue
            try {
                val m = metadata.metaFor(dir, name)
                out.add(m.copy(dateDownload = dates[name] ?: m.dateDownload))
            } catch (_: Exception) {
            }
        }
        return out
    }

    /**
     * Today's 50-song mix, generating + persisting once per day.
     * Same-day reads return the stored order (stable across restarts).
     * Purges non-today rows on generation.
     */
    suspend fun getDailyMix(): DailyMix = withContext(Dispatchers.IO) {
        val today = today()
        try {
            val row = db.dailyMixDao().getByDate(today)
            val stored = row?.songFiles?.let { parseFiles(it) } ?: emptyList()
            if (stored.isNotEmpty()) {
                val dir = musicDir()
                val existing = stored.filter { File(dir, it).exists() }
                if (existing.isNotEmpty()) {
                    return@withContext DailyMix(today, resolve(existing), cached = true)
                }
            }
        } catch (_: Exception) {
        }
        // Generate fresh.
        return@withContext try {
            val all = allFiles()
            val ranked = rankedFiles(all.toSet())
            val picked = buildMixFiles(all, ranked)
            try {
                db.dailyMixDao().purgeNonToday(today)
                db.dailyMixDao().save(
                    DailyMixEntity(
                        mixDate = today,
                        songFiles = JSONArray(picked).toString(),
                        createdAt = STAMP.format(Date())
                    )
                )
            } catch (_: Exception) {
            }
            DailyMix(today, resolve(picked), cached = false)
        } catch (_: Exception) {
            DailyMix(today, emptyList(), cached = false)
        }
    }

    /** Most recently played unique songs, newest first (home strip). */
    suspend fun recentlyPlayed(limit: Int = 15): List<SongMeta> = withContext(Dispatchers.IO) {
        val n = limit.coerceAtLeast(1)
        val files = mutableListOf<String>()
        try {
            db.openHelper.readableDatabase.query(
                "SELECT song_file FROM Music_History ORDER BY date_played DESC, id DESC"
            ).use { c ->
                val seen = HashSet<String>()
                val dir = musicDir()
                while (c.moveToNext() && files.size < n) {
                    val f = c.getString(0) ?: continue
                    if (!seen.add(f)) continue
                    if (File(dir, f).exists()) files.add(f)
                }
            }
        } catch (_: Exception) {
        }
        resolve(files)
    }

    /** Most recently added songs, newest first (home strip). */
    suspend fun recentlyDownloaded(limit: Int = 15): List<SongMeta> = withContext(Dispatchers.IO) {
        val n = limit.coerceAtLeast(1)
        val dir = musicDir()
        if (!dir.exists()) return@withContext emptyList()
        val dates = try {
            db.songDao().getAll().associate { it.file to it.dateDownload }
        } catch (_: Exception) {
            emptyMap()
        }
        val files = dir.listFiles { f -> f.isFile && f.extension.equals("mp3", ignoreCase = true) }
            ?.toList() ?: emptyList()
        val ordered = files.sortedByDescending { f ->
            val raw = dates[f.name]
            if (!raw.isNullOrBlank()) {
                try {
                    val s = raw.replace('T', ' ').trimEnd('Z')
                    SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).apply {
                        timeZone = TimeZone.getTimeZone("UTC")
                    }.parse(s)?.time ?: f.lastModified()
                } catch (_: Exception) {
                    f.lastModified()
                }
            } else f.lastModified()
        }.take(n).map { it.name }
        resolve(ordered)
    }

    companion object {
        private val DATE_FMT = SimpleDateFormat("yyyy-MM-dd", Locale.US).apply {
            timeZone = TimeZone.getDefault()
        }
        private val STAMP = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }

        fun parseFiles(json: String): List<String> {
            return try {
                val arr = JSONArray(json)
                List(arr.length()) { arr.getString(it) }
            } catch (_: Exception) {
                emptyList()
            }
        }
    }
}
