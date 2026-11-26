package com.musicplayer.android.ui

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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.musicplayer.android.R
import com.musicplayer.android.data.api.SyncConfig
import com.musicplayer.android.ui.theme.HathorColors
import com.musicplayer.android.ui.theme.HathorSwitch
import com.musicplayer.android.ui.theme.HathorTextField
import com.musicplayer.android.ui.theme.HathorTonalButton
import com.musicplayer.android.ui.theme.hathorGlass
import kotlinx.coroutines.launch

/**
 * Full-screen Web account gate.
 *
 * Credentials are sent only to /auth/login; the password is cleared from the
 * form on success and never persisted.
 */
@Composable
fun LoginScreen(
    initialUrl: String,
    initialUsername: String,
    onSignedIn: () -> Unit,
    allowContinueWithKey: Boolean = false,
    onContinueWithKey: () -> Unit = {},
) {
    var url by remember { mutableStateOf(initialUrl) }
    var username by remember { mutableStateOf(initialUsername) }
    var password by remember { mutableStateOf("") }
    var showPassword by remember { mutableStateOf(false) }
    var rememberMe by remember { mutableStateOf(true) }
    var busy by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val focus = LocalFocusManager.current

    val canSubmit = !busy &&
        url.isNotBlank() &&
        username.isNotBlank() &&
        password.isNotBlank()

    fun finishSuccess(text: String) {
        password = ""
        error = false
        message = text
        focus.clearFocus()
        onSignedIn()
    }

    fun signIn() {
        if (!canSubmit) return
        busy = true
        error = false
        message = null
        scope.launch {
            val result = SyncConfig.login(url, username, password, rememberMe)
            busy = false
            if (result.ok) {
                finishSuccess(result.message)
            } else {
                error = true
                message = result.message
                password = ""
            }
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(HathorColors.Background)
            .statusBarsPadding()
            .navigationBarsPadding()
            .imePadding(),
        contentAlignment = Alignment.Center,
    ) {
        // Same ambient orange glows as the desktop/web login.
        Box(
            modifier = Modifier
                .offset(x = (-110).dp, y = (-180).dp)
                .size(280.dp)
                .background(
                    Brush.radialGradient(
                        colors = listOf(HathorColors.GlowTop, Color.Transparent)
                    )
                )
        )
        Box(
            modifier = Modifier
                .offset(x = 150.dp, y = 230.dp)
                .size(240.dp)
                .background(
                    Brush.radialGradient(
                        colors = listOf(HathorColors.GlowBottom, Color.Transparent)
                    )
                )
        )

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .widthIn(max = 460.dp)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 22.dp, vertical = 28.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(18.dp),
        ) {
            Image(
                painter = painterResource(R.drawable.app_logo),
                contentDescription = "Hathor",
                modifier = Modifier.size(104.dp)
            )
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(
                    "Hathor",
                    style = MaterialTheme.typography.titleLarge.copy(fontSize = 30.sp),
                    color = HathorColors.TextPrimary
                )
                Text(
                    "Your library, everywhere.",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
            }

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(24.dp))
                    .background(HathorColors.SurfaceGlass)
                    .border(1.dp, HathorColors.BorderFaint, RoundedCornerShape(24.dp))
                    .padding(20.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(
                    "Sign in",
                    style = MaterialTheme.typography.titleMedium,
                    color = HathorColors.TextPrimary
                )
                Text(
                    "Use the same account as the Hathor web app.",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint
                )
                HathorTextField(
                    value = url,
                    onValueChange = {
                        url = it
                        error = false
                    },
                    label = "Server",
                    placeholder = "https://hathor.example or http://192.168.1.10:5051",
                    leadingIcon = { Icon(Icons.Filled.Dns, contentDescription = null) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                HathorTextField(
                    value = username,
                    onValueChange = {
                        username = it
                        error = false
                    },
                    label = "Username",
                    leadingIcon = { Icon(Icons.Filled.Person, contentDescription = null) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                HathorTextField(
                    value = password,
                    onValueChange = {
                        password = it
                        error = false
                    },
                    label = "Password",
                    leadingIcon = { Icon(Icons.Filled.Lock, contentDescription = null) },
                    trailingIcon = {
                        IconButton(onClick = { showPassword = !showPassword }) {
                            Icon(
                                if (showPassword) Icons.Filled.VisibilityOff else Icons.Filled.Visibility,
                                contentDescription = if (showPassword) "Hide password" else "Show password"
                            )
                        }
                    },
                    visualTransformation = if (showPassword) {
                        VisualTransformation.None
                    } else {
                        PasswordVisualTransformation()
                    },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    Text(
                        "Keep me signed in for 30 days",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextBody
                    )
                    HathorSwitch(checked = rememberMe, onCheckedChange = { rememberMe = it })
                }

                message?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = if (error) Color(0xFFFCA5A5) else HathorColors.AccentBright
                    )
                }

                Button(
                    onClick = { signIn() },
                    enabled = canSubmit,
                    modifier = Modifier.fillMaxWidth().height(50.dp),
                    shape = RoundedCornerShape(14.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = HathorColors.Accent,
                        contentColor = Color.White,
                        disabledContainerColor = HathorColors.Accent.copy(alpha = 0.4f),
                        disabledContentColor = Color.White.copy(alpha = 0.55f),
                    ),
                ) {
                    Text(if (busy) "Signing in…" else "Sign in", fontWeight = FontWeight.SemiBold)
                }
                if (allowContinueWithKey) {
                    HathorTonalButton(
                        onClick = onContinueWithKey,
                        enabled = !busy,
                        modifier = Modifier.fillMaxWidth()
                    ) { Text("Continue with saved API key") }
                    Text(
                        "A legacy API key is already configured on this device. Continue with it, or sign in above to use your Web account.",
                        style = MaterialTheme.typography.bodySmall,
                        color = HathorColors.TextHint,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth()
                    )
                }
                Text(
                    "The password is sent only to your server and is never stored. " +
                        "The rotating refresh token is kept in the Android Keystore.",
                    style = MaterialTheme.typography.bodySmall,
                    color = HathorColors.TextHint,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth()
                )

            }
            Spacer(Modifier.height(2.dp))
            Text(
                "Registration is managed in the web app. " +
                    "Passwords never leave this device except for the sign-in request.",
                style = MaterialTheme.typography.bodySmall,
                color = HathorColors.TextHint,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth()
            )
        }
    }
}
