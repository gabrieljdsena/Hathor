package com.musicplayer.android.data

import android.app.Application
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.local.AppDatabase
import com.musicplayer.android.engine.DownloadEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
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
class PlaylistRepositoryTest {

    private lateinit var db: AppDatabase
    private lateinit var repo: PlaylistRepository

    private fun cleanDatabase() = runBlocking(Dispatchers.IO) {
        db.openHelper.writableDatabase.apply {
            execSQL("DELETE FROM song_playlist")
            execSQL("DELETE FROM playlists")
            execSQL("DELETE FROM songs")
        }
    }

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = AppDatabase.get(context)
        cleanDatabase()
        repo = PlaylistRepository(context, MetadataRepository(context, DownloadEngine(context)))
    }

    @After
    fun tearDown() {
        cleanDatabase()
    }

    @Test
    fun `playlist membership and removal use the same links table`() = runTest {
        val first = repo.create("First", null)
        val second = repo.create("Second", null)

        assertTrue(repo.setSongPlaylists("a.mp3", "A", listOf(first)))
        assertEquals(listOf(first), repo.idsForSong("a.mp3"))

        assertTrue(repo.setSongPlaylists("a.mp3", "A", listOf(first, second)))
        assertEquals(setOf(first, second), repo.idsForSong("a.mp3").toSet())

        // Removing from one playlist must keep the other association.
        repo.removeSong(first, "a.mp3")
        assertEquals(listOf(second), repo.idsForSong("a.mp3"))

        // Clearing the set removes every association.
        assertTrue(repo.setSongPlaylists("a.mp3", "A", emptyList()))
        assertTrue(repo.idsForSong("a.mp3").isEmpty())
    }
}
