# AndroidPlayer — Phase 1 (engine-proving APK)

Native Kotlin app. Phase 1 goal: **prove a real YouTube → MP3 download on-device**
using `youtubedl-android` (real yt-dlp) + its bundled `:ffmpeg` artifact. No sync,
no playlists yet — those land only after this APK produces an actual `.mp3`.

## What was scaffolded

- `settings.gradle.kts` / `build.gradle.kts` / `gradle.properties`
- `gradle/libs.versions.toml` — `youtubedlAndroid = "0.18.1"`
  (`io.github.junkfood02.youtubedl-android:library` + `:ffmpeg`, Maven Central —
  coordinates verified against upstream README + Maven Central)
- `app/` module: `minSdk 24`, `targetSdk/compileSdk 34`, ABIs `arm64-v8a + x86_64`,
  `extractNativeLibs=true`, `INTERNET` permission
- `PlayerApp` — inits `YoutubeDL` + `FFmpeg` once, exposes Ready/Failed state
- `engine/DownloadEngine` — desktop `Download.py` semantics:
  `-f bestaudio/best -x --audio-format mp3 -o <app-music-dir>/%(title)s.%(ext)s`,
  progress + logcat-visible log, returns the real output `File` (size > 0 = pass)
- `MainActivity` — one-screen test: paste URL → Download → progress bar → OK/FAILED + log

Output goes to the app-specific Music dir (`getExternalFilesDir(MUSIC)/AndroidPlayer`),
so no storage permission / SAF is needed for Phase 1. (SAF picker = Phase 3.)

## How to build (this machine has no Android SDK yet)

This PC currently has: Java 17 ✓, Android SDK ✗, Android Studio ✗, Gradle ✗.

1. Install **Android Studio** (it brings the SDK + Gradle automatically):
   https://developer.android.com/studio
2. Open this folder (`C:\Users\gabriel\Desktop\idk\AndroidPlayer`) in Android Studio.
3. Let it sync Gradle, then **Run ▶** on a phone (arm64) or emulator (x86_64).
4. Paste your known-good test URL, tap **Download as MP3**.
5. **Pass =** `OK: …/AndroidPlayer/<title>.mp3 (<N> bytes)`. The file is playable
   from the device's file manager / Studio Device Explorer.

## Still needed from you

- ~~The **one known-good YouTube test URL**~~ — provided, pre-filled in the app.
- **Phase 2 (Sync tab) needs your remote DB fields.** Add to `local.properties`
  (same values as your desktop `.env`, never committed):
  ```
  sdk.dir=C:\\Android\\Sdk
  DB_HOST=<your tidb/mysql host>
  DB_PORT=4000
  DB_USER=<user>
  DB_PASSWORD=<password>
  DB_NAME=<database>
  ```
  Then rebuild + reinstall. Empty `DB_HOST` = sync shows the same
  "No remote DB configured" message as desktop. Tap **Sync from remote DB**:
  pass = real song count appears, missing files download with per-song status,
  summary reads `Synced N new entries… OK: x, failed: y`.
