package com.musicplayer.android.ui

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.ByteArrayOutputStream

@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class PlaylistCoverTest {

    private fun sourcePng(width: Int, height: Int): ByteArray {
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val out = ByteArrayOutputStream()
        assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, out))
        bitmap.recycle()
        return out.toByteArray()
    }

    private fun bounds(bytes: ByteArray): BitmapFactory.Options =
        BitmapFactory.Options().apply {
            inJustDecodeBounds = true
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, this)
        }

    @Test
    fun `large covers are scaled down to the maximum dimension`() {
        val scaled = downscaleCoverBytes(sourcePng(1600, 800), maxPx = 200)
        requireNotNull(scaled)

        val opts = bounds(scaled)
        assertEquals(200, opts.outWidth)
        assertEquals(100, opts.outHeight)
    }

    @Test
    fun `small covers keep their dimensions`() {
        val scaled = downscaleCoverBytes(sourcePng(120, 60), maxPx = 800)
        requireNotNull(scaled)

        val opts = bounds(scaled)
        assertEquals(120, opts.outWidth)
        assertEquals(60, opts.outHeight)
    }
}
