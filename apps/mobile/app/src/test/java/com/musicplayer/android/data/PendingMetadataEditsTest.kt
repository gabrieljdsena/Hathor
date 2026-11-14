package com.musicplayer.android.data

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class PendingMetadataEditsTest {

    @After
    fun tearDown() {
        PendingMetadataEdits.clear()
    }

    private fun edit(file: String, title: String = "Title") = PendingMetadataEdit(
        file = file,
        title = title,
        artist = "Artist",
        album = "Album",
        year = "2024",
        cover = null,
        removeCover = false
    )

    @Test
    fun `clearFor only drops the matching file`() {
        PendingMetadataEdits.stage(edit("a.mp3"))

        PendingMetadataEdits.clearFor("b.mp3")
        assertNotNull(PendingMetadataEdits.edit.value)

        PendingMetadataEdits.clearFor("a.mp3")
        assertNull(PendingMetadataEdits.edit.value)
    }

    @Test
    fun `staging replaces the previous pending edit`() {
        PendingMetadataEdits.stage(edit("a.mp3", "First"))
        PendingMetadataEdits.stage(edit("b.mp3", "Second"))

        assertEquals("b.mp3", PendingMetadataEdits.edit.value?.file)
        assertEquals("Second", PendingMetadataEdits.edit.value?.title)
    }
}
