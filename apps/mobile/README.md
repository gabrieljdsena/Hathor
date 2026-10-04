# Hathor — Android app

> Part of the [Hathor monorepo](../../README.md) (`apps/mobile`). Desktop: `../desktop/`, web: `../web/`.

Native Android client for Hathor: the same music library, downloads, playlists,
history and sync as the desktop app, rebuilt with Kotlin, Jetpack Compose and
Media3. The visual system matches desktop and web — near-black `zinc-950`
background, glass surfaces, orange accent.

## What it does

- **Library** — Home dashboard, All Songs list with search and sort, Daily Mix,
  artist/album views, per-song menu (play, queue, edit, playlists, delete)
- **Playback** — foreground `PlayerService` (media-playback type) with queue,
  shuffle/repeat and a Now Playing sheet; audio previews via `PreviewPlayer`
- **YouTube downloads** — on-device YouTube → 320kbps MP3 using the real yt-dlp
  engine (`youtubedl-android` + its bundled FFmpeg), with a job list showing
  per-download progress, retry and cancel
- **Podcasts** — separate podcast library with its own storage folder
- **Playlists & history** — playlist CRUD, played/download history tabs
- **Remote sync** — pulls the library from the same MySQL/TiDB database the
  desktop app pushes to, so the phone mirrors the PC collection

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
   DB_HOST=<your tidb/mysql host>
   DB_PORT=4000
   DB_USER=<user>
   DB_PASSWORD=<password>
   DB_NAME=<database>
   ```

   Same values as the desktop `.env`. Leaving `DB_HOST` empty disables sync —
   the app runs fully offline with local downloads and playback.
3. **Run ▶** on a phone or emulator.

## Project structure

```text
apps/mobile/
  settings.gradle.kts / build.gradle.kts / gradle.properties
  gradle/libs.versions.toml          dependency catalog
  app/
    build.gradle.kts                 applicationId com.musicplayer.android, version 0.3.0-hathor
    src/main/AndroidManifest.xml     INTERNET + foreground-service permissions, PlayerService
    src/main/java/com/musicplayer/android/
      MainActivity.kt / PlayerApp.kt application entry, yt-dlp + FFmpeg init
      engine/DownloadEngine.kt       YouTube → MP3 download engine (yt-dlp options mirror desktop Download.py)
      playback/                      PlayerService, PlayerManager, PlaybackSource, PreviewPlayer
      ui/                            Compose screens: Home, Library/AllSongs, DailyMix, Download,
                                     Podcasts, Playlists, History, Settings, NowPlayingSheet,
                                     SongRow, VisualizerBars
      ui/shell/HathorShell.kt        navigation rail + destinations (Home, Download, Podcasts,
                                     Playlists, History, Settings; All Songs / Daily Mix under Home)
      ui/theme/                      Hathor colors, typography, motion (desktop visual tokens)
```

## How sync works

The app talks directly to the remote MySQL/TiDB database (Connector/J) using
the same table and column names as the desktop `database.sql`, and keeps a
local Room copy for offline use. Background sync is scheduled with WorkManager.
Credentials are baked in from `local.properties` at build time via
`BuildConfig` fields — they never appear in source. Tombstone deletions,
incremental history and newest-wins mixes follow `packages/contracts`
(`../../packages/contracts/`).

## Current status

The Compose UI, navigation shell, theme, download engine and playback service
are in the repo. The `data` package (Room database, repositories, sync
workers referenced by the screens) has not landed yet, so the project does
not compile on a fresh checkout — that layer is the next piece to push.
`PLAN.md` and `FEATURE_PLAN.md` in this folder are the original build plans
kept for reference; this README describes the app itself.
