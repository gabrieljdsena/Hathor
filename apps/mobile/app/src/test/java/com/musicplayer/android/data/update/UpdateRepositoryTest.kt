package com.musicplayer.android.data.update

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.musicplayer.android.data.api.SyncConfig
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File
import java.security.MessageDigest
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(application = android.app.Application::class)
class UpdateRepositoryTest {

    private lateinit var context: Context
    private lateinit var prefsName: String
    private lateinit var downloads: File
    private lateinit var api: FakeApi
    private lateinit var repo: UpdateRepository
    private var clock = 1_000_000L

    private class FakeApi(
        var result: LatestResult,
        var payload: ByteArray = byteArrayOf(1, 2, 3, 4),
    ) : UpdateApiClient {
        var latestCalls = 0
        var downloadCalls = 0
        override fun latest(): LatestResult {
            latestCalls++
            return result
        }

        override fun download(
            info: UpdateInfo,
            destination: File,
            onProgress: (Long, Long) -> Unit,
        ) {
            downloadCalls++
            destination.writeBytes(payload)
            onProgress(payload.size.toLong(), payload.size.toLong())
        }
    }

    private fun sha(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") {
            (it.toInt() and 0xff).toString(16).padStart(2, '0')
        }

    private fun info(
        code: Int = 6,
        payload: ByteArray = api.payload,
    ) = UpdateInfo(
        versionCode = code,
        versionName = "0.4.3-hathor",
        fileName = "hathor-$code-0.4.3-hathor.apk",
        sizeBytes = payload.size.toLong(),
        sha256 = sha(payload),
        publishedAtUtc = "2026-11-21T01:00:00+00:00",
        downloadPath = "/api/v1/app/download/hathor-$code-0.4.3-hathor.apk",
    )

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        SyncConfig.init(context)
        SyncConfig.save("http://127.0.0.1:1", "test-token")
        prefsName = "update-test-${UUID.randomUUID()}"
        downloads = File(context.cacheDir, "update-test-${UUID.randomUUID()}")
        api = FakeApi(LatestResult.NotFound)
        api.result = LatestResult.Success(info())
        repo = UpdateRepository(
            context = context,
            versionCode = 5,
            prefs = context.getSharedPreferences(prefsName, Context.MODE_PRIVATE),
            downloadsDir = downloads,
            now = { clock },
            apiFactory = { _, _ -> api },
        )
    }

    @After
    fun tearDown() {
        downloads.deleteRecursively()
        context.getSharedPreferences(prefsName, Context.MODE_PRIVATE).edit().clear().commit()
        SyncConfig.clearRuntime()
    }

    @Test
    fun `newer release is available and marks the check`() = runTest {
        val state = repo.checkNow(force = false, silent = false)
        assertTrue(state is UpdateState.Available)
        assertTrue(repo.dialogVisible.value)
        assertEquals(6, (repo.state.value as UpdateState.Available).info.versionCode)
        assertEquals(
            clock,
            context.getSharedPreferences(prefsName, Context.MODE_PRIVATE)
                .getLong(UpdateRepository.KEY_LAST_CHECK, 0L),
        )
    }

    @Test
    fun `equal or lower release never offers an update`() = runTest {
        api.result = LatestResult.Success(info(code = 5))
        assertTrue(repo.checkNow(force = true, silent = false) is UpdateState.UpToDate)
        api.result = LatestResult.Success(info(code = 4))
        assertTrue(repo.checkNow(force = true, silent = false) is UpdateState.UpToDate)
        assertFalse(repo.dialogVisible.value)
    }

    @Test
    fun `throttle skips checks until 24h or force`() = runTest {
        repo.checkNow(force = false, silent = false)
        assertEquals(1, api.latestCalls)

        clock += 23L * 60L * 60L * 1000L
        repo.checkNow(force = false, silent = false)
        assertEquals(1, api.latestCalls)

        clock += 60L * 60L * 1000L // exactly 24h after the first check
        repo.checkNow(force = false, silent = false)
        assertEquals(2, api.latestCalls)

        clock += 1L
        repo.checkNow(force = true, silent = false)
        assertEquals(3, api.latestCalls)
    }

    @Test
    fun `404 marks no release and a failure does not refresh the throttle`() = runTest {
        api.result = LatestResult.NotFound
        assertTrue(repo.checkNow(force = false, silent = false) is UpdateState.NoRelease)
        assertEquals(
            clock,
            context.getSharedPreferences(prefsName, Context.MODE_PRIVATE)
                .getLong(UpdateRepository.KEY_LAST_CHECK, 0L),
        )

        context.getSharedPreferences(prefsName, Context.MODE_PRIVATE).edit().clear().commit()
        api.result = LatestResult.Failure("offline")
        assertTrue(repo.checkNow(force = false, silent = false) is UpdateState.Error)
        assertEquals(
            0L,
            context.getSharedPreferences(prefsName, Context.MODE_PRIVATE)
                .getLong(UpdateRepository.KEY_LAST_CHECK, 0L),
        )
    }

    @Test
    fun `download verifies sha and reuses the verified file`() = runTest {
        repo.checkNow(force = true, silent = false)
        val ready = repo.downloadNow()
        assertTrue(ready is UpdateState.Ready)
        val file = (ready as UpdateState.Ready).file
        assertTrue(file.exists())
        assertEquals(sha(api.payload), repo.sha256(file))
        assertEquals(1, api.downloadCalls)

        val again = repo.downloadNow()
        assertTrue(again is UpdateState.Ready)
        assertEquals(1, api.downloadCalls)
    }

    @Test
    fun `sha mismatch removes the partial and never installs it`() = runTest {
        val good = info()
        api.payload = byteArrayOf(9, 9, 9)
        api.result = LatestResult.Success(good)
        repo.checkNow(force = true, silent = false)

        val state = repo.downloadNow()
        assertTrue(state is UpdateState.Error)
        val target = File(downloads, good.fileName)
        assertFalse(target.exists())
        assertFalse(File(downloads, "${good.fileName}.part").exists())
    }

    @Test
    fun `sha helper is lowercase hex`() {
        val file = File(context.cacheDir, "sha-test-${UUID.randomUUID()}.bin")
        file.writeBytes(byteArrayOf(0, 1, 2, 127, -1))
        assertEquals(sha(file.readBytes()), repo.sha256(file))
        assertTrue(repo.sha256(file).matches(Regex("^[0-9a-f]{64}$")))
        file.delete()
    }
}
