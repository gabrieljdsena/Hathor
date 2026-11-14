package com.musicplayer.android.data.db

import android.app.Application
import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
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

/**
 * Room schema/DAO contract used by sync merges. No device needed: Robolectric
 * provides the Android SQLite driver for an in-memory database.
 *
 * `Application::class` keeps PlayerApp.onCreate out of the JVM test: the real
 * app starts yt-dlp, queue workers and its own database.
 */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class SongDaoTest {

    private lateinit var db: AppDatabase
    private lateinit var songs: SongDao

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        songs = db.songDao()
    }

    @After
    fun tearDown() {
        db.close()
    }

    private fun song(file: String, title: String = file) = SongEntity(
        file = file,
        downloadedLink = null,
        title = title,
        dateDownloadMillis = 1_700_000_000_000,
        artist = "Artist",
    )

    @Test
    fun `insertIgnore keeps the first row for a duplicate file`() = runTest {
        songs.insertIgnore(song("a.mp3", "First"))
        songs.insertIgnore(song("a.mp3", "Second"))

        val row = songs.byFile("a.mp3")
        assertEquals("First", row?.title)
        assertEquals(1, songs.all().size)
    }

    @Test
    fun `all sorts by file name case-insensitively`() = runTest {
        songs.insertIgnore(song("b.mp3"))
        songs.insertIgnore(song("A.mp3"))
        songs.insertIgnore(song("c.mp3"))

        assertEquals(listOf("A.mp3", "b.mp3", "c.mp3"), songs.all().map { it.file })
    }

    @Test
    fun `update rewrites an existing row`() = runTest {
        songs.insertIgnore(song("a.mp3", "Old"))
        songs.update(song("a.mp3", "New").copy(artist = "Other"))

        val row = songs.byFile("a.mp3")
        assertEquals("New", row?.title)
        assertEquals("Other", row?.artist)
    }

    @Test
    fun `deleteByFile removes only the requested row`() = runTest {
        songs.insertIgnore(song("a.mp3"))
        songs.insertIgnore(song("b.mp3"))

        songs.deleteByFile("a.mp3")

        assertNull(songs.byFile("a.mp3"))
        assertEquals(listOf("b.mp3"), songs.all().map { it.file })
    }

    @Test
    fun `playlist rows and song links are stored and cascaded by playlist id`() = runTest {
        val playlists = db.playlistDao()
        val links = db.songPlaylistDao()
        val playlistId = playlists.insertIgnore(
            PlaylistEntity(title = "Mix", description = null, thumbnail = null),
        )
        links.insertIgnore(
            SongPlaylistLink(songFile = "b.mp3", playlistId = playlistId, dateAddedMillis = 2),
        )
        links.insertIgnore(
            SongPlaylistLink(songFile = "a.mp3", playlistId = playlistId, dateAddedMillis = 1),
        )

        assertEquals("Mix", playlists.byId(playlistId)?.title)
        assertEquals(2, links.all().size)

        links.deleteForPlaylists(listOf(playlistId))
        assertTrue(links.all().isEmpty())
    }
}
