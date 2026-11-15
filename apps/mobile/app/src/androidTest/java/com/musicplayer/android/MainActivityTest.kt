package com.musicplayer.android

import android.Manifest
import android.os.Build
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TestRule
import org.junit.runner.Description
import org.junit.runner.RunWith
import org.junit.runners.model.Statement

/**
 * End-to-end shell smoke test. Runs on a device/emulator:
 * `./gradlew connectedDebugAndroidTest`.
 */
@RunWith(AndroidJUnit4::class)
class MainActivityTest {

    /**
     * MainActivity asks for POST_NOTIFICATIONS on API 33+. The system dialog
     * pauses the activity, so Compose never sees a hierarchy; grant it before
     * the activity is launched. No-op on older devices.
     */
    @get:Rule(order = 0)
    val notificationPermissionRule: TestRule = object : TestRule {
        override fun apply(base: Statement, description: Description): Statement =
            object : Statement() {
                override fun evaluate() {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        val instrumentation = InstrumentationRegistry.getInstrumentation()
                        instrumentation.uiAutomation.executeShellCommand(
                            "pm grant ${instrumentation.targetContext.packageName} " +
                                Manifest.permission.POST_NOTIFICATIONS,
                        ).close()
                    }
                    base.evaluate()
                }
            }
    }

    @get:Rule(order = 1)
    val compose = createAndroidComposeRule<MainActivity>()

    @Test(timeout = 180_000)
    fun shellRendersEveryRailDestination() {
        listOf("Home", "Discover", "Download", "Podcasts", "Playlists", "History", "AI DJ", "Settings").forEach { label ->
            compose.onNodeWithContentDescription(label).assertExists()
        }
    }

    @Test(timeout = 180_000)
    fun settingsRailItemOpensSettingsScreen() {
        compose.onNodeWithContentDescription("Settings").performClick()
        compose.waitForIdle()

        compose.onNodeWithText("Manage your preferences").assertIsDisplayed()
    }

    @Test(timeout = 180_000)
    fun homeBrowseCardOpensArtists() {
        compose.onNodeWithText("Browse by artist, artwork and play-all.")
            .performScrollTo()
            .performClick()
        compose.waitForIdle()

        compose.onNodeWithText("No artists match.").assertIsDisplayed()
    }
}
