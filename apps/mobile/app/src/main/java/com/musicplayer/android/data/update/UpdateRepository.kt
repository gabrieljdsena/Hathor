package com.musicplayer.android.data.update

import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.util.Log
import androidx.core.content.FileProvider
import com.musicplayer.android.BuildConfig
import com.musicplayer.android.data.api.SyncConfig
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.security.MessageDigest
import java.util.Locale

enum class RetryAction { CHECK, DOWNLOAD, INSTALL }

sealed interface UpdateState {
    data object Idle : UpdateState
    data object Disabled : UpdateState
    data object Checking : UpdateState
    data object UpToDate : UpdateState
    data object NoRelease : UpdateState
    data class Available(val info: UpdateInfo) : UpdateState
    data class Downloading(val info: UpdateInfo, val bytes: Long, val total: Long) : UpdateState
    data class Ready(val info: UpdateInfo, val file: File) : UpdateState
    data class NeedsPermission(val info: UpdateInfo, val file: File) : UpdateState
    data class Error(
        val message: String,
        val info: UpdateInfo? = null,
        val retry: RetryAction,
    ) : UpdateState
    data class Installing(val info: UpdateInfo) : UpdateState
}

/**
 * App-scoped sideload updater. Owns the 24 h check throttle, downloads,
 * SHA-256 verification and the unknown-sources install retry.
 *
 * All repository methods are safe from the UI thread; the public methods
 * launch on [scope]. The internal suspend methods are used by tests.
 */
class UpdateRepository(
    private val context: Context,
    private val versionCode: Int = BuildConfig.VERSION_CODE,
    private val prefs: SharedPreferences =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE),
    private val downloadsDir: File = File(context.filesDir, "updates"),
    private val now: () -> Long = { System.currentTimeMillis() },
    private val apiFactory: (baseUrl: String, token: String) -> UpdateApiClient =
        { baseUrl, token -> HttpUpdateApi(baseUrl, token) },
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
) {
    private val _state = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = _state.asStateFlow()

    private val _dialogVisible = MutableStateFlow(false)
    val dialogVisible: StateFlow<Boolean> = _dialogVisible.asStateFlow()

    private val mutex = Mutex()

    @Volatile
    private var currentInfo: UpdateInfo? = null

    @Volatile
    private var pendingInstall: File? = null

    /** Startup check: throttled, silent on failures, never blocks the UI. */
    fun checkOnStartup() {
        if (!SyncConfig.isConfigured()) {
            _state.value = UpdateState.Disabled
            return
        }
        if (!shouldCheck(force = false)) return
        scope.launch { checkLocked(force = false, silent = true) }
    }

    /** Manual check (Settings): bypasses the 24 h throttle and shows errors. */
    fun check(force: Boolean = true) {
        if (!SyncConfig.isConfigured()) {
            _state.value = UpdateState.Disabled
            _dialogVisible.value = false
            return
        }
        scope.launch { checkLocked(force, silent = false) }
    }

    fun download() {
        scope.launch { downloadLocked() }
    }

    fun install() {
        val file = pendingInstall ?: (state.value as? UpdateState.Ready)?.file
            ?: (state.value as? UpdateState.NeedsPermission)?.file
            ?: return
        scope.launch { installLocked(file) }
    }

    fun retry() {
        when (val current = state.value) {
            is UpdateState.Error -> when (current.retry) {
                RetryAction.CHECK -> check(force = true)
                RetryAction.DOWNLOAD -> download()
                RetryAction.INSTALL -> install()
            }
            else -> check(force = true)
        }
    }

    fun dismissDialog() {
        _dialogVisible.value = false
    }

    /** Retry after returning from Settings → Install unknown apps. */
    fun onResume() {
        val file = pendingInstall ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            !context.packageManager.canRequestPackageInstalls()
        ) {
            return
        }
        scope.launch { installLocked(file, openSettingsIfNeeded = false) }
    }

    internal suspend fun checkNow(force: Boolean, silent: Boolean): UpdateState =
        checkLocked(force, silent)

    private suspend fun checkLocked(force: Boolean, silent: Boolean): UpdateState {
        if (!SyncConfig.isConfigured()) {
            _state.value = UpdateState.Disabled
            return UpdateState.Disabled
        }
        if (!force && !shouldCheck(force = false)) return _state.value
        mutex.withLock {
            _state.value = UpdateState.Checking
            val api = apiFactory(SyncConfig.baseUrl(), SyncConfig.token())
            when (val result = api.latest()) {
                is LatestResult.Success -> {
                    markChecked()
                    val info = result.info
                    currentInfo = info
                    if (isNewer(info)) {
                        _state.value = UpdateState.Available(info)
                        _dialogVisible.value = true
                    } else {
                        _state.value = UpdateState.UpToDate
                        _dialogVisible.value = false
                    }
                }
                LatestResult.NotFound -> {
                    markChecked()
                    currentInfo = null
                    _state.value = UpdateState.NoRelease
                    _dialogVisible.value = false
                }
                is LatestResult.Failure -> {
                    _state.value = UpdateState.Error(result.message, retry = RetryAction.CHECK)
                    // Startup failures stay silent; manual checks surface them.
                    _dialogVisible.value = !silent
                }
            }
            return _state.value
        }
    }

    internal suspend fun downloadNow(): UpdateState = downloadLocked()

    private suspend fun downloadLocked(): UpdateState = mutex.withLock {
        val info = currentInfo ?: (state.value as? UpdateState.Available)?.info
            ?: (state.value as? UpdateState.Ready)?.info
            ?: return@withLock _state.value
        if (!isNewer(info)) {
            _state.value = UpdateState.UpToDate
            _dialogVisible.value = false
            return@withLock _state.value
        }

        val target = File(downloadsDir, safeFileName(info))
        try {
            downloadsDir.mkdirs()
            cleanup(otherThan = target)
            if (target.exists() && verifyFile(target, info)) {
                _state.value = UpdateState.Ready(info, target)
                _dialogVisible.value = true
                return@withLock _state.value
            }
            target.delete()
            val part = File(downloadsDir, "${target.name}.part")
            part.delete()
            _state.value = UpdateState.Downloading(info, 0L, info.sizeBytes)
            _dialogVisible.value = true
            try {
                apiFactory(SyncConfig.baseUrl(), SyncConfig.token()).download(info, part) { bytes, total ->
                    _state.value = UpdateState.Downloading(info, bytes, total)
                }
                if (!verifyFile(part, info)) {
                    part.delete()
                    throw UpdateException("Downloaded APK failed SHA-256 verification.")
                }
                if (!part.renameTo(target)) {
                    part.copyTo(target, overwrite = true)
                    part.delete()
                }
                cleanup(otherThan = target)
                _state.value = UpdateState.Ready(info, target)
                _dialogVisible.value = true
            } catch (e: Exception) {
                part.delete()
                val message = e.message ?: "Update download failed."
                Log.w(TAG, "download failed: $message", e)
                _state.value = UpdateState.Error(message, info, RetryAction.DOWNLOAD)
                _dialogVisible.value = true
            }
        } catch (e: Exception) {
            val message = e.message ?: "Update download failed."
            Log.w(TAG, "download preparation failed: $message", e)
            _state.value = UpdateState.Error(message, info, RetryAction.DOWNLOAD)
            _dialogVisible.value = true
        }
        _state.value
    }

    internal suspend fun installNow(file: File, openSettingsIfNeeded: Boolean = true): Boolean =
        installLocked(file, openSettingsIfNeeded)

    private suspend fun installLocked(
        file: File,
        openSettingsIfNeeded: Boolean = true,
    ): Boolean = withContext(Dispatchers.Main) {
        val info = currentInfo ?: return@withContext false
        if (!file.exists() || !verifyFile(file, info)) {
            _state.value = UpdateState.Error(
                "The downloaded APK is missing or corrupt. Download it again.",
                info,
                RetryAction.DOWNLOAD,
            )
            _dialogVisible.value = true
            return@withContext false
        }

        val pm = context.packageManager
        val archive = archiveInfo(pm, file)
        if (archive == null || archive.first != context.packageName ||
            archive.second != info.versionCode.toLong() ||
            !isNewer(info)
        ) {
            _state.value = UpdateState.Error(
                "The downloaded APK does not match this Hathor build.",
                info,
                RetryAction.DOWNLOAD,
            )
            _dialogVisible.value = true
            return@withContext false
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            !pm.canRequestPackageInstalls()
        ) {
            pendingInstall = file
            _state.value = UpdateState.NeedsPermission(info, file)
            _dialogVisible.value = true
            if (openSettingsIfNeeded) {
                val settings = Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:${context.packageName}"),
                ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                try {
                    context.startActivity(settings)
                } catch (e: Exception) {
                    Log.w(TAG, "cannot open unknown-sources settings", e)
                    _state.value = UpdateState.Error(
                        "Open Settings → Apps → Hathor → Install unknown apps, then retry.",
                        info,
                        RetryAction.INSTALL,
                    )
                    _dialogVisible.value = true
                }
            }
            return@withContext false
        }

        val uri = try {
            FileProvider.getUriForFile(
                context,
                "${context.packageName}.fileprovider",
                file,
            )
        } catch (e: Exception) {
            _state.value = UpdateState.Error(
                "Could not expose the APK to the installer: ${e.message}",
                info,
                RetryAction.INSTALL,
            )
            _dialogVisible.value = true
            return@withContext false
        }
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, APK_MIME)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        try {
            context.startActivity(intent)
            pendingInstall = null
            _state.value = UpdateState.Installing(info)
            _dialogVisible.value = false
            true
        } catch (e: Exception) {
            Log.w(TAG, "cannot launch installer", e)
            _state.value = UpdateState.Error(
                "No APK installer is available: ${e.message}",
                info,
                RetryAction.INSTALL,
            )
            _dialogVisible.value = true
            false
        }
    }

    internal fun isNewer(info: UpdateInfo): Boolean = info.versionCode > versionCode

    internal fun shouldCheck(force: Boolean): Boolean {
        if (force) return true
        val last = prefs.getLong(KEY_LAST_CHECK, 0L)
        return last <= 0L || now() - last >= CHECK_INTERVAL_MS
    }

    internal fun verifyFile(file: File, info: UpdateInfo): Boolean {
        if (!file.isFile || file.length() <= 0L) return false
        if (info.sizeBytes > 0 && file.length() != info.sizeBytes) return false
        return sha256(file).equals(info.sha256.lowercase(Locale.US), ignoreCase = true)
    }

    internal fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") {
            (it.toInt() and 0xff).toString(16).padStart(2, '0')
        }
    }

    private fun markChecked() {
        prefs.edit().putLong(KEY_LAST_CHECK, now()).apply()
    }

    private fun cleanup(otherThan: File) {
        val keep = otherThan.name
        downloadsDir.listFiles()?.forEach { file ->
            if (file.isFile && file.name != keep &&
                (file.name.endsWith(".apk", true) || file.name.endsWith(".apk.part", true))
            ) {
                file.delete()
            }
        }
    }

    private fun safeFileName(info: UpdateInfo): String {
        val serverName = info.fileName
        if (serverName.length <= 255 && serverName.endsWith(".apk", true) &&
            !serverName.contains('/') && !serverName.contains('\\') && !serverName.contains("..")
        ) {
            return serverName
        }
        val version = info.versionName.replace(Regex("[^A-Za-z0-9._-]"), "_")
        return "hathor-${info.versionCode}-$version.apk"
    }

    /** Package name + long versionCode from a downloaded APK, or null. */
    private fun archiveInfo(pm: PackageManager, file: File): Pair<String, Long>? {
        val info = try {
            pm.getPackageArchiveInfo(file.absolutePath, 0)
        } catch (_: Exception) {
            null
        } ?: return null
        @Suppress("DEPRECATION")
        val code = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            info.longVersionCode
        } else {
            info.versionCode.toLong()
        }
        return info.packageName to code
    }

    companion object {
        private const val TAG = "UpdateRepository"
        const val PREFS = "hathor_updates"
        const val KEY_LAST_CHECK = "last_check_ms"
        const val CHECK_INTERVAL_MS = 24L * 60L * 60L * 1000L
        const val APK_MIME = "application/vnd.android.package-archive"
    }
}
