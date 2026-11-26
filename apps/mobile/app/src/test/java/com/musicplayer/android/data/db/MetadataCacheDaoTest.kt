package com.musicplayer.android.data.db

import android.app.Application
import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.local.MetadataCacheEntity
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class MetadataCacheDaoTest {

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
    fun `metadata cache round trips and deletes stale paths`() = runTest {
        val dao = db.metadataCacheDao()
        val first = MetadataCacheEntity(
            path = "/music/a.mp3",
            lastModified = 10L,
            fileLength = 20L,
            title = "A",
            artist = "Artist",
            album = "Album",
            year = "2026",
            durationSec = 42L,
            hasCover = false
        )
        val stale = first.copy(path = "/music/gone.mp3")
        dao.putAll(listOf(first, stale))

        val all = dao.getAll().associateBy { it.path }
        assertEquals(first, all["/music/a.mp3"])

        dao.deletePaths(listOf(stale.path))
        assertEquals(setOf(first.path), dao.getAll().map { it.path }.toSet())
        assertTrue(dao.getAll().isNotEmpty())
    }
}
