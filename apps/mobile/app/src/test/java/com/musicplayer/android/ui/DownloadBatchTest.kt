package com.musicplayer.android.ui

import org.junit.Assert.assertEquals
import org.junit.Test

class DownloadBatchTest {

    @Test
    fun `parses one url per line and ignores comments`() {
        val text = """
            # my queue
            https://youtu.be/a
            https://youtu.be/b  # trailing text is dropped
            not a url
            https://youtu.be/a
        """.trimIndent()

        assertEquals(listOf("https://youtu.be/a", "https://youtu.be/b"), parseBatchUrls(text))
    }

    @Test
    fun `ignores blank files`() {
        assertEquals(emptyList<String>(), parseBatchUrls("\n \n# only comments\n"))
    }
}
