package com.musicplayer.android.data

import android.app.Application
import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.data.remote.RemoteSync
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class PodcastRepositoryTest {

    private lateinit var db: AppDatabase
    private lateinit var repo: PodcastRepository

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val engine = DownloadEngine(context)
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        repo = PodcastRepository(context, engine, MetadataRepository(context, engine), db)
    }

    @After
    fun tearDown() {
        db.close()
    }

    @Test
    fun `unassign records link tombstone and re-assign clears it`() = runTest {
        val id = repo.createTag("Tech")
        assertTrue(id > 0)
        assertTrue(repo.assignTag("ep.mp3", id))
        assertEquals(1, db.podcastTagLinkDao().getLinks().size)

        assertTrue(repo.unassignTag("ep.mp3", id))
        assertTrue(db.podcastTagLinkDao().getLinks().isEmpty())
        val tombstones = db.syncDeletionDao().all()
        assertEquals(listOf("podcast_tag_links"), tombstones.map { it.tableName })
        assertEquals("$id:ep.mp3", tombstones.single().rowKey)

        assertTrue(repo.assignTag("ep.mp3", id))
        assertTrue(db.syncDeletionDao().all().none { it.tableName == "podcast_tag_links" })
        assertEquals(1, db.podcastTagLinkDao().getLinks().size)
    }

    @Test
    fun `link tombstone key splits on the first colon`() {
        assertEquals(7L to "ep:1.mp3", RemoteSync.parseTagLinkKey("7:ep:1.mp3"))
        assertNull(RemoteSync.parseTagLinkKey("ep.mp3"))
        assertNull(RemoteSync.parseTagLinkKey(":ep.mp3"))
        assertNull(RemoteSync.parseTagLinkKey("7:"))
        assertNull(RemoteSync.parseTagLinkKey("x:ep.mp3"))
    }
}
