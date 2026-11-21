# Hathor — Android app

> Part of the [Hathor monorepo](../../README.md) (`apps/mobile`). Desktop: `../desktop/`, web: `../web/`.

Native Android client for Hathor: a personal music system that brings your
library, YouTube downloads, playlists, podcasts, daily mixes, and cross-device
history into one player. Built with Kotlin, Jetpack Compose, and Media3, with
the same near-black `zinc-950`, glass, and orange visual language as desktop
and web.

## What it does

- **Library** — Home dashboard, All Songs list with search and sort, Daily Mix,
  artist/album views, per-song menu (play, queue, edit, playlists, delete)
- **Playback** — foreground `PlayerService` (media-playback type) with queue,
  shuffle/repeat and a Now Playing sheet; audio previews via `PreviewPlayer`
- **YouTube downloads** — on-device YouTube → 320kbps MP3 using the real yt-dlp
  engine (`youtubedl-android` + its bundled FFmpeg), with a job list showing
  per-download progress, retry and cancel
- **Podcasts** — separate podcast library with its own storage folder,
  per-episode chapter marks (seek chips, edit, delete) with optional
  chapter auto-skip (Settings), and podcast tags
- **Lyrics** — full-screen synced/plain lyrics with active-line follow and
  tap-to-seek, ±20 s timing correction, romaji, search/replace/edit, and
  per-chapter podcast lyrics; no drawer.
- **Playlists & history** — playlist CRUD, played/download history tabs
- **Web API-backed features** — Discover recommendations and the AI DJ
  (Vohithe) run on the Hathor Web API; the phone renders the feed/sets and
  plays returned files. Vohithe's commentary clips are played over ducked
  music, and the vibe can be typed or spoken (server transcription). No login
  is required — configure a scoped `hth_` key in Settings.
- **Playback telemetry** — every non-podcast track play posts one
  completion/skip segment to `/player/playback-events` in the background, so
  Discover/AI DJ recommendations reflect phone listening. Delivery is
  best-effort and never blocks local playback.
- **Server sync** — pulls the library from the Hathor server over the LAN
  (delta + file bytes), so the phone mirrors the PC collection
  (songs, podcasts, playlists, lyrics incl. highlight offsets, podcast
  chapters, tags, history, daily mix); pushes local rows and new file
  bytes back the same way. Playback history also flushes automatically
  through a durable `History_Sync_Queue` outbox; full catalog/files push
  remains manual.

## Requirements

- **Android Studio** (brings the Android SDK + Gradle) — or a standalone
  SDK with `sdk.dir` set (see below)
- Java 17, Android SDK platform 34
- A physical arm64 phone or an x86_64 emulator (`minSdk 24`, ABIs
  `arm64-v8a` + `x86_64`)
- No storage permission needed: files live in the app-specific Music
  directory

## Run it

1. Open this folder (`apps/mobile`) in Android Studio and let Gradle sync.
2. Create a `local.properties` file next to `settings.gradle.kts` (never
   committed — see `.gitignore`):

   ```properties
   sdk.dir=C:\\Android\\Sdk
   API_BASE_URL=http://192.168.1.10:5051
   API_TOKEN=hth_<per-device key with library:read + library:write>
   ```

   These are **build-time defaults**. You can also set the server URL and API
   key at runtime under **Settings → Web API** (no rebuild, no login). Keys for
   Discover/sync need `library:read` + `library:write`; add `player:read` +
   `player:control` for AI DJ, cross-device playback and playback telemetry.
   Leaving both empty disables server features — the app still runs offline
   with local downloads and playback.
3. **Run ▶** on a phone or emulator.

## Develop & test

Prerequisites: **JDK 17**, **Android SDK platform 34 + build-tools 34.0.0**
(platform-tools gives you `adb`). The repo ships the Gradle wrapper
(`gradlew` / `gradlew.bat`, Gradle 8.7) — no global Gradle install needed.

```powershell
# one-time SDK packages (skip what you already have)
sdkmanager "platform-tools" "platforms;android-34" "build-tools;34.0.0"

# build + install the debug APK on the connected device
.\gradlew.bat :app:assembleDebug
adb install -r app\build\outputs\apk\debug\app-debug.apk
adb shell am start -n com.musicplayer.android/.MainActivity
```

### Devices

**BlueStacks 5** (Pie64, Android 9 / API 28, x86_64) is fine for manual runs:

1. In BlueStacks: **Settings → Advanced → Android Debug Bridge** → enable it.
2. Connect adb to the instance (default port `5555`):

   ```powershell
   adb connect 127.0.0.1:5555
   adb devices           # expect 127.0.0.1:5555  device
   ```

3. Keep BlueStacks running while Gradle installs and drives the app.

> **Known BlueStacks limitation:** installing an instrumentation (androidTest)
> APK makes BlueStacks 5 terminate the instance on this machine, so
> `connectedDebugAndroidTest` cannot drive it. Use the AVD below (or a
> physical phone) for automation and BlueStacks for manual UI work.

**Android emulator (recommended for tests).** One-time setup:

```powershell
sdkmanager "emulator" "system-images;android-34;google_apis;x86_64"
avdmanager create avd -n hathor_api34 `
  -k "system-images;android-34;google_apis;x86_64" -d pixel_6

# headless boot (WHPX acceleration required; check with: emulator -accel-check)
emulator -avd hathor_api34 -no-window -no-audio -no-boot-anim `
  -gpu swiftshader_indirect -no-snapshot
```

The app ships `x86_64` and `arm64-v8a` native binaries, so the AVD, BlueStacks
and physical phones all work.

### Automated tests

```powershell
# fast JVM suite: no device needed (Robolectric)
.\gradlew.bat testDebugUnitTest

# instrumentation suite on a running device/emulator (Compose UI smoke tests)
.\gradlew.bat connectedDebugAndroidTest

# convenience wrapper: unit tests, then optionally device tests
.\scripts\test.ps1
.\scripts\test.ps1 -Device
.\scripts\test.ps1 -Device -Avd hathor_api34     # boots the AVD if needed
.\scripts\test.ps1 -Device -Serial <adb-serial>  # phone / BlueStacks
```

Coverage today:

- `SearchQueryTest` — linkless download search query building (title/artist
  recovery, whitespace, "Unknown" placeholders).
- `LyricsParserTest` — LRC `[mm:ss.xx]` parsing, empty-line placeholder,
  malformed-line skipping.
- `SyncApiTest` — sync contract decoding (`parseSnapshot`, chapter pseudo-lyrics
  filtering, `missingFiles` casings, ISO timestamps).
- `SongDaoTest` — Room schema/DAO contract (upsert-ignore, ordering, deletes,
  playlist links) on an in-memory database.
- `PlaybackTelemetryTest` — completed/skipped classification, podcast exclusion,
  track changes and pause/resume segment handling.
- `PlaybackEventsApiTest` — POST path, bearer auth and JSON body against a local
  socket server.
- `HistorySyncOutboxTest` — Room v10 outbox, natural-key lookup, id-collision
  fallback and bounded manual-push clearing.
- `SyncRepositoryHistoryTest` — queued history posts a scoped import and clears
  the outbox after acceptance, against a local socket server.
- `MainActivityTest` (`androidTest`) — shell renders every rail destination and
  Settings opens.

CI runs the JVM suite on pushes/PRs touching `apps/mobile`
(`.github/workflows/mobile-tests.yml`). The instrumentation suite runs locally
on an AVD/phone because hosted emulators are slow and flaky; wire it into CI
with `reactivecircus/android-emulator-runner` if you need it.

## In-app updates (sideload channel)

The phone updates itself from the Hathor server over the LAN — no Play Store.
The server side is implemented (`apps/web/src/Hathor.Api/MobileAppController.cs`,
deploy notes in [`../web/DEPLOY.md`](../web/DEPLOY.md)); the Android client and
release scripts are described here.

### Server contract

Reuse the server URL/token from **Settings → Web API**
(`SyncConfig.baseUrl()` / `SyncConfig.token()`); both calls need
`Authorization: Bearer <hth_ key or JWT>`.

```http
GET /api/v1/app/latest
```

```json
{
  "versionCode": 6,
  "versionName": "0.4.3-hathor",
  "fileName": "hathor-6-0.4.3-hathor.apk",
  "sizeBytes": 132456789,
  "sha256": "lowercase-hex",
  "publishedAtUtc": "2026-11-20T18:00:00+00:00",
  "downloadPath": "/api/v1/app/download/hathor-6-0.4.3-hathor.apk"
}
```

`404 { "message": "…" }` when no APK has been published. Compare
`versionCode` with `BuildConfig.VERSION_CODE`; equal/lower means up to date.
Then:

```http
GET /api/v1/app/download/{fileName}
```

Returns the APK as `application/vnd.android.package-archive` with range
support. `downloadPath` is relative to the same server base URL. Verify the
`sha256` (lowercase hex) before installing and delete the file on mismatch.

### Expected app behaviour

- Check on launch (throttled to once per 24 h in SharedPreferences) **and** a
  manual "Check for updates" row in **Settings → About**; skip silently when
  the Web API is not configured.
- Show a dialog with current/latest version. Download to `filesDir/updates/`
  with progress, verify SHA-256, then install through a `FileProvider`
  (`content://`) and `Intent.ACTION_VIEW` with the APK MIME type.
- Declare `REQUEST_INSTALL_PACKAGES`. On API 26+, if
  `packageManager.canRequestPackageInstalls()` is false, open
  `Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES` for the app and retry after the
  user returns.
- Never downgrade or reinstall the same `versionCode`; keep at most one
  downloaded APK and clean it up after the installer is launched.
- Use the existing stdlib stack (`HttpURLConnection` + `org.json`, like
  `data/api/SyncApi.kt`) — no new dependencies. The APK is large
  (native yt-dlp/FFmpeg), so stream to a `.part` file and use a longer read
  timeout.

### Release signing (one-time, on the work PC)

The update installer requires every APK to be signed with the **same key** as
the installed app. Debug builds use a machine-local debug key, so pick one:

- **Dedicated release key (recommended)** — one-time uninstall of the debug
  install (back up the library first, see below).
- **Reuse the existing debug keystore** (`%USERPROFILE%\.android\debug.keystore`,
  alias `androiddebugkey`, password `android`) — upgrades in place, but it is
  a well-known key. Fine for a homelab only.
- **Losing the key means no in-place updates** — back up the `.jks` and its
  passwords outside the repo.

Generate the dedicated key (keep it outside the repo, e.g. `C:\keys`):

```powershell
keytool -genkeypair -v -keystore C:\keys\hathor-release.jks `
  -alias hathor -keyalg RSA -keysize 4096 -validity 10000
```

Create `apps/mobile/keystore.properties` (gitignored):

```properties
storeFile=C:\\keys\\hathor-release.jks
storePassword=<password>
keyAlias=hathor
keyPassword=<password>
```

Wire it into `app/build.gradle.kts` as `signingConfigs.release` (read the
properties file when present, fall back to unsigned otherwise). The existing
debug install is signed with a different key, so the first release APK must be
installed after a one-time migration:

```powershell
# 1. Back up the app-private music + podcast libraries (deleted by uninstall)
New-Item -ItemType Directory -Force .\android-player-backup\Music, .\android-player-backup\Podcasts | Out-Null
adb pull /sdcard/Android/data/com.musicplayer.android/files/Music/AndroidPlayer .\android-player-backup\Music
adb pull /sdcard/Android/data/com.musicplayer.android/files/Podcasts/AndroidPlayer .\android-player-backup\Podcasts

# 2. Remove the debug install (signature mismatch otherwise)
adb uninstall com.musicplayer.android

# 3. Install the first release-signed APK, then restore the libraries
adb install .\dist\hathor-6-0.4.3-hathor.apk
adb push .\android-player-backup\Music\. /sdcard/Android/data/com.musicplayer.android/files/Music/AndroidPlayer/
adb push .\android-player-backup\Podcasts\. /sdcard/Android/data/com.musicplayer.android/files/Podcasts/AndroidPlayer/
```

The internal database (playlists/history/settings) is **not** in those folders.
Push the library to the server before uninstalling if you want it restored by
a Pull afterward; app settings such as volume and wallpaper will reset.

Alternatively, use Settings before step 2 to move the music/podcasts folders
to a shared (SAF) folder that survives uninstall, then re-pick it afterwards.
The server library can also be re-pulled.

### Compile & publish (work PC)

1. Bump `versionCode` (strictly increasing) and `versionName` in
   `app/build.gradle.kts` — e.g. `6` / `0.4.3-hathor`.
2. Confirm `local.properties` has `sdk.dir`, `API_BASE_URL` (server LAN URL)
   and `API_TOKEN` (scope `library:read` + `library:write`). These values are
   baked into the APK; do not commit them.
3. Build the signed release:

   ```powershell
   .\gradlew.bat :app:assembleRelease
   ```

   Target convenience wrapper: `.\scripts\release.ps1` (create it if missing)
   should build, rename/copy the output to
   `dist\hathor-<versionCode>-<versionName>.apk`, print the SHA-256 and accept
   `-Destination \\SERVER\HathorAPK` to publish directly (the SMB share is
   created by the server's `apps/web/scripts/share-apk.ps1`).
4. Expected Gradle output:
   `app\build\outputs\apk\release\app-release.apk`. Publish exactly one file
   named `hathor-<versionCode>-<versionName>.apk`; the server ignores
   everything else and selects the highest `versionCode`.
5. Verify end-to-end (with a valid `hth_` key):

   ```powershell
   Invoke-RestMethod -Uri http://<server>:5051/api/v1/app/latest `
     -Headers @{ Authorization = 'Bearer hth_...' }
   ```

   The returned `versionCode`/`sha256` must match the file just copied.
6. Run `.\gradlew.bat testDebugUnitTest` before publishing, and optionally
   `adb install -r .\hathor-<code>-<name>.apk` to smoke-test the installer.

Never commit `.jks`, `keystore.properties`, `dist/` APKs or `local.properties`.

## Project structure

```text
apps/mobile/
  settings.gradle.kts / build.gradle.kts / gradle.properties
  gradle/libs.versions.toml          dependency catalog
  app/
    build.gradle.kts                 applicationId com.musicplayer.android, version 0.4.14-hathor
    src/main/AndroidManifest.xml     INTERNET + foreground-service permissions, PlayerService
    src/main/java/com/musicplayer/android/
      MainActivity.kt / PlayerApp.kt application entry, yt-dlp + FFmpeg init
      engine/DownloadEngine.kt       YouTube → MP3 download engine (yt-dlp options mirror desktop Download.py)
      playback/                      PlayerService, PlayerManager, PlaybackTelemetry, PlaybackSource, PreviewPlayer
      data/update/                   in-app sideload updater (check, download, install)
      ui/                            Compose screens: Home, Library/AllSongs, DailyMix, Download,
                                     Podcasts, Playlists, History, Settings, NowPlayingSheet,
                                     SongRow, VisualizerBars
      ui/shell/HathorShell.kt        navigation rail + destinations (Home, Download, Podcasts,
                                     Playlists, History, Settings; All Songs / Daily Mix under Home)
      ui/theme/                      Hathor colors, typography, motion (desktop visual tokens)
  scripts/release.ps1               signed release APK + optional SMB publish
```

## How sync works

The app talks to the Hathor Web API over the LAN (`data/api/SyncApi.kt`,
stdlib `HttpURLConnection` + `org.json` — no extra dependencies), using the
same table semantics as the desktop `database.sql` (see
`data/db/Entities.kt`), and keeps a local Room copy (`hathor.db`) for
offline use. Server URL + API token come from **Settings → Web API**, with
`local.properties` / `BuildConfig` as build-time defaults; they never appear in
source. Empty values disable server features.

- **Pull** (`data/remote/PullWorker.kt`, shared by the first-run prompt and
  the Settings Pull button): `GET /api/v1/sync/delta?cursor=` (incremental —
  cursor persists in the `hathor` prefs, advancing only on full success),
  upserts into Room, applies server tombstones (rows + bytes), adopts the
  mix, downloads missing files byte-identical under exact server filenames.
  One bad row never aborts.
- **Push** (`data/SyncRepository.kt` + `data/remote/RemoteWriter.kt`,
  Settings Push button): rows + tombstones via `POST /api/v1/sync/import`,
  missing bytes via `PUT /api/v1/sync/files/{file}`, tombstones cleared only
  on server accept. Step-by-step progress, summary and errors surface
  through `SyncState`.
- Push/pull are 1:1 with desktop `services/api_sync.py`. No scheduler
  anywhere: pull is first-run prompt + Pull button, push is the Push button
  (manual-only, like desktop).

## Current status

The app builds and both test suites pass (JVM/Robolectric + Compose). Sync,
local playback/downloads, metadata, playlists, podcasts, Artists/Albums,
Discover and AI DJ are wired. AI DJ v1 uses the text brief and server-selected
batches; server voice clips/transcription are not played yet. Cross-device
resume is still pending.

`WEB_PARITY.md` tracks the web→mobile gap backlog and what remains
(runtime Web API, Discover, AI DJ and cross-device resume are implemented;
AI DJ voice clips/auto-advance and optional polish remain).
