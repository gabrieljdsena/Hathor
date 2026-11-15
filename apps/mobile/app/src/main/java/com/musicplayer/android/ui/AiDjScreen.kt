package com.musicplayer.android.ui

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.util.Base64
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.musicplayer.android.R
import com.musicplayer.android.data.MetadataRepository
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.data.VibeRecorder
import com.musicplayer.android.data.api.AiDjApi
import com.musicplayer.android.data.api.AiDjBatch
import com.musicplayer.android.playback.AiDjController
import com.musicplayer.android.playback.AiDjUiState
import com.musicplayer.android.playback.DjVoiceStatus
import com.musicplayer.android.playback.PlayerManager
import com.musicplayer.android.ui.aidj.AudioWaveform
import com.musicplayer.android.ui.aidj.VohitheVisualizer
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.util.Locale

/**
 * Vohithe / AI DJ. A mobile translation of the web view: hero name, portrait
 * and radial visualizer, status, vibe card, Now Playing, Next Set and Voice.
 * Batch internals and commentary stay on the server and are not dumped here.
 */
@Composable
fun AiDjScreen(
    controller: AiDjController,
    player: PlayerManager,
    metadata: MetadataRepository
) {
    val ui by controller.ui.collectAsState()
    val currentFile by player.current.collectAsState()
    val isPlaying by player.playing.collectAsState()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val recorder = remember { VibeRecorder() }

    var brief by remember { mutableStateOf("") }
    var nowMeta by remember { mutableStateOf<SongMeta?>(null) }
    var nowCover by remember { mutableStateOf<Bitmap?>(null) }
    var recorderError by remember { mutableStateOf<String?>(null) }
    var transcribing by remember { mutableStateOf(false) }
    val recording by recorder.recording.collectAsState()

    LaunchedEffect(Unit) { controller.refresh() }
    LaunchedEffect(currentFile) {
        val file = currentFile
        if (file == null) {
            nowMeta = null
            nowCover = null
            return@LaunchedEffect
        }
        withContext(Dispatchers.IO) {
            try {
                nowMeta = metadata.metaFor(file)
                nowCover = metadata.coverArtFor(file, 192)
            } catch (_: Exception) {
                nowMeta = null
                nowCover = null
            }
        }
    }
    DisposableEffect(Unit) {
        onDispose { recorder.cancel() }
    }

    fun finishRecording() {
        if (transcribing) return
        scope.launch {
            transcribing = true
            recorderError = null
            try {
                val wav = recorder.stop()
                if (wav.size < 2_000) {
                    recorderError = "That was too short — try again."
                } else {
                    val b64 = Base64.encodeToString(wav, Base64.NO_WRAP)
                    val language = ui.server?.settings?.language
                        ?.takeIf { it.isNotBlank() && it != "auto" }
                    val text = withContext(Dispatchers.IO) {
                        AiDjApi.transcribe(b64, language)
                    }
                    if (text.isBlank()) {
                        recorderError = "Vohithe could not hear that. Try again."
                    } else {
                        brief = if (brief.isBlank()) text else "$brief $text"
                    }
                }
            } catch (e: Exception) {
                recorderError = e.message ?: "Transcription failed."
            } finally {
                transcribing = false
            }
        }
    }

    fun startRecording() {
        recorderError = null
        try {
            recorder.start()
        } catch (e: Exception) {
            recorderError = e.message ?: "Could not open the microphone."
        }
    }

    val micPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (granted) startRecording()
        else recorderError = "Microphone permission is needed to speak the vibe."
    }

    fun toggleMic() {
        if (recording) {
            finishRecording()
        } else if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) ==
            PackageManager.PERMISSION_GRANTED
        ) {
            startRecording()
        } else {
            micPermission.launch(Manifest.permission.RECORD_AUDIO)
        }
    }

    AiDjContent(
        ui = ui,
        currentMeta = nowMeta,
        currentCover = nowCover,
        isPlaying = isPlaying,
        brief = brief,
        onBriefChange = { brief = it },
        recording = recording,
        transcribing = transcribing,
        recorderError = recorderError,
        onMic = { toggleMic() },
        onStart = { controller.start(brief) },
        onStop = { controller.stop() },
        onFeedback = { controller.feedback(it) }
    )
}

@Composable
internal fun AiDjContent(
    ui: AiDjUiState,
    currentMeta: SongMeta?,
    currentCover: Bitmap?,
    isPlaying: Boolean,
    brief: String,
    onBriefChange: (String) -> Unit,
    recording: Boolean,
    transcribing: Boolean,
    recorderError: String?,
    onMic: () -> Unit,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onFeedback: (String) -> Unit,
    animateVisualizer: Boolean = true
) {
    val active = ui.active
    val voiceSpeaking = ui.voice == DjVoiceStatus.Speaking
    val waiting = ui.voice == DjVoiceStatus.Waiting
    val onAir = active
    val server = ui.server
    val batch = ui.currentBatch ?: server?.currentBatch
    val next = ui.nextBatch ?: server?.nextBatch
    val language = languageLabel(server?.settings?.language)
    val statusLabel = when {
        waiting -> "Waiting for voice"
        voiceSpeaking -> "Speaking"
        active -> "On air"
        else -> "Ready"
    }
    val error = (ui.error ?: recorderError)
        ?.takeUnless { it.equals("null", ignoreCase = true) }
    val warning = when {
        server != null && !server.enabled -> "AI DJ is disabled on the server."
        server != null && server.enabled && !server.configured ->
            "AI DJ is enabled but not configured on the server."
        else -> null
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(18.dp)
    ) {
        Eyebrow()
        Text(
            "Vohithe",
            style = TextStyle(
                fontSize = 58.sp,
                lineHeight = 56.sp,
                fontWeight = FontWeight.Bold,
                letterSpacing = (-2.5).sp,
                color = HathorColors.TextPrimary
            )
        )
        StatusRow(
            status = statusLabel,
            active = onAir,
            voice = voiceSpeaking,
            waiting = waiting,
            language = language,
            setNumber = batch?.number
        )
        PortraitHero(
            active = active,
            voice = voiceSpeaking,
            waiting = waiting,
            status = statusLabel,
            animateVisualizer = animateVisualizer
        )
        VibeCard(
            brief = brief,
            onBriefChange = onBriefChange,
            active = active,
            busy = ui.busy,
            enabled = server?.enabled == true,
            recording = recording,
            transcribing = transcribing,
            onMic = onMic,
            onStart = onStart,
            onStop = onStop,
            onFeedback = onFeedback,
            error = error,
            warning = warning
        )
        NowPlayingCard(
            meta = currentMeta,
            cover = currentCover,
            isPlaying = isPlaying,
            trackCount = batch?.tracks?.size ?: 0
        )
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            NextSetCard(
                next = next,
                modifier = Modifier.weight(1f)
            )
            VoiceCard(
                active = active || voiceSpeaking || waiting,
                status = statusLabel,
                language = language,
                animateVisualizer = animateVisualizer,
                modifier = Modifier.weight(1f)
            )
        }
    }
}

@Composable
private fun Eyebrow() {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        Text(
            "HATHOR PRIVATE RADIO",
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                letterSpacing = 2.4.sp,
                color = HathorColors.TextHint
            )
        )
        Box(
            modifier = Modifier
                .width(30.dp)
                .height(1.dp)
                .background(HathorColors.Accent.copy(alpha = 0.5f))
        )
        Text(
            "AI DJ",
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                letterSpacing = 2.4.sp,
                color = HathorColors.AccentBright.copy(alpha = 0.8f)
            )
        )
    }
}

@Composable
private fun StatusRow(
    status: String,
    active: Boolean,
    voice: Boolean,
    waiting: Boolean,
    language: String,
    setNumber: Int?
) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(9.dp)
    ) {
        StatusDot(active, waiting, voice)
        Text(
            status.uppercase(Locale.US),
            style = StatusStyle,
            color = HathorColors.TextBody
        )
        Text("/", style = StatusStyle, color = HathorColors.TextHint.copy(alpha = 0.5f))
        Text(language, style = StatusStyle, color = HathorColors.TextBody)
        if (setNumber != null) {
            Text("/", style = StatusStyle, color = HathorColors.TextHint.copy(alpha = 0.5f))
            Text(
                "SET ${setNumber.toString().padStart(2, '0')}",
                style = StatusStyle,
                color = HathorColors.TextBody
            )
        }
    }
}

private val StatusStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontSize = 11.sp,
    letterSpacing = 1.7.sp
)

@Composable
private fun StatusDot(active: Boolean, waiting: Boolean, voice: Boolean) {
    val color = when {
        waiting -> Color(0xFFFBBF24)
        voice -> Color(0xFFFB923C)
        active -> Color(0xFF34D399)
        else -> Color(0xFF52525B)
    }
    Box(modifier = Modifier.size(8.dp).clip(CircleShape).background(color))
}

@Composable
private fun PortraitHero(
    active: Boolean,
    voice: Boolean,
    waiting: Boolean,
    status: String,
    animateVisualizer: Boolean
) {
    Box(
        modifier = Modifier.fillMaxWidth().height(320.dp),
        contentAlignment = Alignment.Center
    ) {
        Box(
            modifier = Modifier
                .size(270.dp)
                .background(
                    Brush.radialGradient(
                        listOf(HathorColors.Accent.copy(alpha = 0.13f), Color.Transparent)
                    ),
                    CircleShape
                )
        )
        VohitheVisualizer(
            active = active,
            voice = voice,
            modifier = Modifier.fillMaxSize(),
            animate = animateVisualizer
        )
        Box(
            modifier = Modifier
                .size(210.dp)
                .clip(CircleShape)
                .background(Color(0xFF18181B))
                .border(1.dp, Color.White.copy(alpha = 0.16f), CircleShape)
        ) {
            Image(
                painter = painterResource(R.drawable.vohithe_portrait),
                contentDescription = "Vohithe",
                contentScale = ContentScale.Crop,
                modifier = Modifier.fillMaxSize()
            )
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(
                        Brush.verticalGradient(
                            0f to Color.Transparent,
                            0.65f to Color.Transparent,
                            1f to Color(0x66000000)
                        )
                    )
            )
        }
        Box(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .clip(RoundedCornerShape(50))
                .background(Color(0xE609090B))
                .border(1.dp, Color.White.copy(alpha = 0.10f), RoundedCornerShape(50))
                .padding(horizontal = 14.dp, vertical = 7.dp)
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(7.dp)
            ) {
                StatusDot(active, waiting, voice)
                Text(
                    status.uppercase(Locale.US),
                    style = StatusStyle.copy(fontSize = 10.sp, letterSpacing = 1.9.sp),
                    color = HathorColors.TextBody
                )
            }
        }
    }
}

@Composable
private fun VibeCard(
    brief: String,
    onBriefChange: (String) -> Unit,
    active: Boolean,
    busy: Boolean,
    enabled: Boolean,
    recording: Boolean,
    transcribing: Boolean,
    onMic: () -> Unit,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onFeedback: (String) -> Unit,
    error: String?,
    warning: String?
) {
    Column(
        modifier = Modifier.fillMaxWidth().hathorGlass().padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        Text(
            "SET THE VIBE",
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                letterSpacing = 2.2.sp,
                color = HathorColors.TextHint
            )
        )
        HathorTextField(
            value = brief,
            onValueChange = onBriefChange,
            modifier = Modifier.fillMaxWidth(),
            singleLine = false,
            minLines = 3,
            maxLines = 5,
            placeholder = "warm evening indie, no sad songs, more female vocals"
        )
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            OutlinedButton(
                onClick = onMic,
                enabled = !transcribing,
                contentPadding = androidx.compose.foundation.layout.PaddingValues(
                    horizontal = 12.dp,
                    vertical = 8.dp
                ),
                colors = ButtonDefaults.outlinedButtonColors(
                    contentColor = if (recording) Color(0xFFFCA5A5) else HathorColors.TextBody
                ),
                border = androidx.compose.foundation.BorderStroke(
                    1.dp,
                    if (recording) Color(0x66EF4444) else Color.White.copy(alpha = 0.12f)
                )
            ) {
                if (transcribing) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(14.dp),
                        strokeWidth = 2.dp,
                        color = HathorColors.AccentBright
                    )
                } else {
                    Icon(
                        Icons.Filled.Mic,
                        contentDescription = null,
                        modifier = Modifier.size(16.dp)
                    )
                }
                Spacer(Modifier.width(6.dp))
                Text(
                    when {
                        transcribing -> "Transcribing"
                        recording -> "Listening"
                        else -> "Speak the vibe"
                    },
                    style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                    color = if (recording) Color(0xFFFCA5A5) else Color.White
                )
            }
        }
        if (active) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FeedbackPill("More like this", onClick = { onFeedback("like") })
                FeedbackPill("Less like this", onClick = { onFeedback("dislike") })
            }
        }
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Spacer(Modifier.weight(1f))
            if (active) {
                OutlinedButton(
                    onClick = onStop,
                    enabled = !busy,
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(
                        horizontal = 12.dp,
                        vertical = 8.dp
                    )
                ) {
                    Text(
                        "Stop",
                        style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                        color = HathorColors.TextBody
                    )
                }
            }
            Button(
                onClick = onStart,
                enabled = !busy && enabled,
                contentPadding = androidx.compose.foundation.layout.PaddingValues(
                    horizontal = 14.dp,
                    vertical = 8.dp
                ),
                colors = ButtonDefaults.buttonColors(
                    containerColor = HathorColors.Accent,
                    contentColor = Color.White
                )
            ) {
                if (busy) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(14.dp),
                        strokeWidth = 2.dp,
                        color = Color.White
                    )
                    Spacer(Modifier.width(6.dp))
                } else {
                    Icon(
                        if (active) Icons.Filled.AutoAwesome else Icons.Filled.PlayArrow,
                        contentDescription = null,
                        modifier = Modifier.size(16.dp)
                    )
                    Spacer(Modifier.width(6.dp))
                }
                Text(
                    when {
                        busy -> "Starting"
                        active -> "New set"
                        else -> "Start"
                    },
                    style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                    color = Color.White
                )
            }
        }
        warning?.let { Notice(it, amber = true) }
        error?.let { Notice(it, amber = false) }
    }
}

@Composable
private fun FeedbackPill(text: String, onClick: () -> Unit) {
    OutlinedButton(
        onClick = onClick,
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            horizontal = 10.dp,
            vertical = 5.dp
        ),
        border = androidx.compose.foundation.BorderStroke(1.dp, Color.White.copy(alpha = 0.10f))
    ) {
        Text(
            text,
            style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
            color = HathorColors.TextBody
        )
    }
}

@Composable
private fun Notice(text: String, amber: Boolean) {
    val color = if (amber) Color(0xFFFBBF24) else Color(0xFFFCA5A5)
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(color.copy(alpha = 0.08f))
            .border(1.dp, color.copy(alpha = 0.18f), RoundedCornerShape(10.dp))
            .padding(horizontal = 10.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text,
            style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
            color = color
        )
    }
}

@Composable
private fun NowPlayingCard(
    meta: SongMeta?,
    cover: Bitmap?,
    isPlaying: Boolean,
    trackCount: Int
) {
    Column(
        modifier = Modifier.fillMaxWidth().hathorGlass().padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                "NOW PLAYING",
                style = TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontSize = 10.sp,
                    letterSpacing = 2.0.sp,
                    color = HathorColors.TextHint
                ),
                modifier = Modifier.weight(1f)
            )
            Text(
                if (trackCount > 0) "$trackCount TRACK SET" else "IDLE",
                style = TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontSize = 9.sp,
                    letterSpacing = 1.4.sp,
                    color = HathorColors.TextHint.copy(alpha = 0.7f)
                )
            )
        }
        if (meta == null) {
            Text(
                "Waiting for the first track.",
                style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                color = HathorColors.TextHint,
                modifier = Modifier.padding(vertical = 18.dp)
            )
        } else {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                val shape = RoundedCornerShape(12.dp)
                Box(
                    modifier = Modifier
                        .size(62.dp)
                        .clip(shape)
                        .background(Color(0xFF1B1B1F)),
                    contentAlignment = Alignment.Center
                ) {
                    if (cover != null) {
                        Image(
                            bitmap = cover.asImageBitmap(),
                            contentDescription = null,
                            contentScale = ContentScale.Crop,
                            modifier = Modifier.fillMaxSize()
                        )
                    } else {
                        Icon(
                            Icons.Filled.MusicNote,
                            contentDescription = null,
                            tint = HathorColors.TextHint
                        )
                    }
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        meta.title.ifBlank { meta.file },
                        style = MaterialTheme.typography.titleMedium.copy(fontSize = 19.sp),
                        maxLines = 1
                    )
                    Text(
                        meta.artist.ifBlank { "Unknown" },
                        style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
                        color = HathorColors.TextHint,
                        maxLines = 1
                    )
                    if (isPlaying) {
                        Text(
                            "PLAYING",
                            style = TextStyle(
                                fontFamily = FontFamily.Monospace,
                                fontSize = 9.sp,
                                letterSpacing = 1.6.sp,
                                color = HathorColors.AccentBright.copy(alpha = 0.85f)
                            )
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun NextSetCard(next: AiDjBatch?, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.hathorGlass().padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Text(
            "NEXT SET",
            style = TextStyle(
                fontFamily = FontFamily.Monospace,
                fontSize = 10.sp,
                letterSpacing = 1.8.sp,
                color = HathorColors.TextHint
            )
        )
        Text(
            next?.tracks?.size?.toString()?.padStart(2, '0') ?: "--",
            style = TextStyle(
                fontSize = 30.sp,
                fontWeight = FontWeight.SemiBold,
                color = HathorColors.TextPrimary
            )
        )
        Text(
            if (next != null) "tracks prepared" else "waiting for a set",
            style = MaterialTheme.typography.bodySmall.copy(fontSize = 13.sp),
            color = HathorColors.TextHint
        )
        Box(Modifier.height(1.dp).fillMaxWidth().background(Color.White.copy(alpha = 0.05f)))
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(6.dp)
                    .clip(CircleShape)
                    .background(if (next != null) Color(0xFFFB923C) else Color(0xFF3F3F46))
            )
            Text(
                if (next != null) "Ready for the transition" else "Not prepared",
                style = TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontSize = 9.sp,
                    letterSpacing = 0.9.sp,
                    color = HathorColors.TextHint
                )
            )
        }
    }
}

@Composable
private fun VoiceCard(
    active: Boolean,
    status: String,
    language: String,
    animateVisualizer: Boolean,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier.hathorGlass().padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                "VOICE",
                style = TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontSize = 10.sp,
                    letterSpacing = 1.8.sp,
                    color = HathorColors.TextHint
                ),
                modifier = Modifier.weight(1f)
            )
            Icon(
                Icons.Filled.Mic,
                contentDescription = null,
                tint = HathorColors.Accent.copy(alpha = 0.75f),
                modifier = Modifier.size(16.dp)
            )
        }
        AudioWaveform(
            active = active,
            modifier = Modifier.fillMaxWidth().height(46.dp),
            animate = animateVisualizer
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                status.uppercase(Locale.US),
                style = TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontSize = 9.sp,
                    letterSpacing = 1.0.sp,
                    color = HathorColors.TextHint
                ),
                modifier = Modifier.weight(1f)
            )
            Text(
                language,
                style = TextStyle(
                    fontFamily = FontFamily.Monospace,
                    fontSize = 9.sp,
                    letterSpacing = 1.0.sp,
                    color = HathorColors.TextHint
                )
            )
        }
    }
}

private fun languageLabel(language: String?): String = when {
    language.isNullOrBlank() || language == "auto" -> "AUTO"
    language.equals("pt-BR", ignoreCase = true) -> "PT-BR"
    else -> "EN-US"
}
