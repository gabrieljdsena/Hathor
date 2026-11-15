package com.musicplayer.android.ui

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performScrollTo
import com.musicplayer.android.data.SongMeta
import com.musicplayer.android.data.api.AiDjBatch
import com.musicplayer.android.data.api.AiDjSettings
import com.musicplayer.android.data.api.AiDjState
import com.musicplayer.android.data.api.AiDjTrack
import com.musicplayer.android.playback.AiDjUiState
import com.musicplayer.android.playback.DjVoiceStatus
import com.musicplayer.android.ui.theme.HathorTheme
import org.junit.Rule
import org.junit.Test

class AiDjScreenTest {

    @get:Rule
    val compose = createComposeRule()

    private fun settings() = AiDjSettings(
        batchSize = 3,
        commentaryMode = "always",
        language = "en-US",
        discoveryLevel = 0.35,
        brief = "",
        preferredGenres = "",
        avoidArtists = "",
        duckLevel = 0.18,
        voiceLeadSeconds = 10
    )

    private fun batch(number: Int) = AiDjBatch(
        id = "batch-$number",
        number = number,
        status = "ready",
        tracks = listOf(
            AiDjTrack("a.mp3", "Breathe Deeper", "Tame Impala", "The Slow Rush", 227)
        ),
        introText = "Intro",
        introClipIds = listOf("clip-$number"),
        introDurationSec = 5.0,
        outroText = "Outro",
        outroClipIds = listOf("outro-$number"),
        outroDurationSec = 6.0,
        shouldComment = true,
        mood = "warm",
        reason = "Because it fits"
    )

    private fun state(active: Boolean, disabled: Boolean = false) = AiDjState(
        enabled = !disabled,
        configured = !disabled,
        sessionId = if (active) "session-1" else null,
        status = if (active) "active" else "stopped",
        settings = settings(),
        currentBatch = if (active) batch(2) else null,
        nextBatch = if (active) batch(3) else null,
        error = null
    )

    private fun content(ui: AiDjUiState) {
        compose.setContent {
            HathorTheme {
                AiDjContent(
                    ui = ui,
                    currentMeta = SongMeta(
                        file = "a.mp3",
                        title = "Breathe Deeper",
                        artist = "Tame Impala",
                        album = "The Slow Rush",
                        durationSec = 227,
                        dateDownload = null
                    ),
                    currentCover = null,
                    isPlaying = ui.active,
                    brief = "",
                    onBriefChange = {},
                    recording = false,
                    transcribing = false,
                    recorderError = null,
                    onMic = {},
                    onStart = {},
                    onStop = {},
                    onFeedback = {},
                    animateVisualizer = false
                )
            }
        }
    }

    @Test
    fun readyScreenShowsHeroAndStart() {
        content(AiDjUiState(loading = false, server = state(active = false)))
        compose.onNodeWithText("Vohithe").assertIsDisplayed()
        compose.onAllNodesWithText("READY").assertCountEquals(3)
        compose.onNodeWithText("Start").assertIsDisplayed()
        compose.onNodeWithText("SET THE VIBE").assertExists()
    }

    @Test
    fun onAirScreenShowsSpeakingAndSetControls() {
        content(
            AiDjUiState(
                loading = false,
                server = state(active = true),
                active = true,
                voice = DjVoiceStatus.Speaking,
                currentBatch = batch(2),
                nextBatch = batch(3)
            )
        )
        compose.onNodeWithText("Vohithe").assertIsDisplayed()
        compose.onAllNodesWithText("SPEAKING").assertCountEquals(3)
        compose.onNodeWithText("New set").assertIsDisplayed()
        compose.onNodeWithText("Stop").assertIsDisplayed()
        compose.onAllNodesWithText("null").assertCountEquals(0)
        compose.onNodeWithText("NOW PLAYING").assertExists()
    }

    @Test
    fun disabledServerShowsOneCompactWarning() {
        content(
            AiDjUiState(
                loading = false,
                server = state(active = false, disabled = true)
            )
        )
        compose.onNodeWithText("AI DJ is disabled on the server.")
            .performScrollTo()
            .assertIsDisplayed()
    }
}
