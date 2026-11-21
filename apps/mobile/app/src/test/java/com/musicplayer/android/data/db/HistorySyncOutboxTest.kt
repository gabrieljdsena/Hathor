package com.musicplayer.android.data.db

import android.app.Application
import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.HistorySyncQueueEntity
import com.musicplayer.android.data.local.MusicHistoryEntity
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class HistorySyncOutboxTest {

    private lateinit var db: AppDatabase

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
    }

    @After
    fun tearDown() {
        db.close()
    }

    @Test
    fun `history outbox queues and clears local rows`() = runTest {
        val id = db.playlistDao().insertMusicHistory(
            MusicHistoryEntity(songFile = "a.mp3", datePlayed = "2026-11-15 12:00:00")
        )
        db.historySyncQueueDao().enqueue(HistorySyncQueueEntity(id))
        db.historySyncQueueDao().enqueue(HistorySyncQueueEntity(id)) // idempotent

        assertEquals(listOf(id), db.historySyncQueueDao().pendingIds())
        assertEquals(
            "a.mp3",
            db.playlistDao().musicHistoryByIds(listOf(id)).single().songFile,
        )

        db.historySyncQueueDao().clear(listOf(id))
        assertTrue(db.historySyncQueueDao().pendingIds().isEmpty())
    }

    @Test
    fun `natural key lookup and id collision fallback mirror the pull merge`() = runTest {
        val first = db.playlistDao().insertMusicHistory(
            MusicHistoryEntity(songFile = "a.mp3", datePlayed = "2026-11-15 12:00:00")
        )
        assertNotNull(
            db.playlistDao().musicHistoryIdFor("a.mp3", "2026-11-15 12:00:00")
        )

        // A server id already used by a different local row must not overwrite it.
        val conflict = db.playlistDao().insertMusicHistoryIgnore(
            MusicHistoryEntity(first, "b.mp3", "2026-11-15 12:00:01")
        )
        assertEquals(-1L, conflict)

        val rekeyed = db.playlistDao().insertMusicHistoryIgnore(
            MusicHistoryEntity(0, "b.mp3", "2026-11-15 12:00:01")
        )
        assertTrue(rekeyed > 0)
        assertEquals(2, db.playlistDao().allMusicHistoryOrdered().size)
    }

    @Test
    fun `manual push clears only rows included in its snapshot`() = runTest {
        val first = db.playlistDao().insertMusicHistory(
            MusicHistoryEntity(songFile = "a.mp3", datePlayed = "2026-11-15 12:00:00")
        )
        val second = db.playlistDao().insertMusicHistory(
            MusicHistoryEntity(songFile = "b.mp3", datePlayed = "2026-11-15 12:00:01")
        )
        db.historySyncQueueDao().enqueue(HistorySyncQueueEntity(first))
        db.historySyncQueueDao().enqueue(HistorySyncQueueEntity(second))

        db.historySyncQueueDao().clearUpTo(first)
        assertEquals(listOf(second), db.historySyncQueueDao().pendingIds())
    }
}
