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
the same table and column names as the desktop `database.sql` (see
`data/db/Entities.kt` — byte-identical names, no translation layer), and
keeps a local Room copy (`hathor.db`) for offline use. Credentials are baked
in from `local.properties` at build time via `BuildConfig` fields — they
never appear in source. Empty `DB_HOST` disables sync, exactly like desktop.

- **Pull** (`data/remote/PullWorker.kt`, shared by the first-run prompt and
  the Settings Pull button): reads songs + podcasts + playlists + links +
  lyrics + both histories + tags + daily mix (guarded tables for old
  remotes), upserts into Room, adopts the remote mix, downloads missing
  files under their exact remote filenames. One bad row never aborts.
  Tombstones are not applied on pull (desktop parity — deletions propagate
  through push; the rows are already gone remotely).
- **Push** (`data/SyncRepository.kt`, Settings Push button): initializes the
  remote schema, upserts songs/podcasts/playlists/tags/lyrics, replaces link
  tables scoped to this phone's ids, propagates tombstones, pushes the mix
  newest-wins with prune, appends history past remote `MAX(id)` with
  `AUTO_INCREMENT` realignment, then clears applied tombstones. Step-by-step
  progress, summary and errors surface through `SyncState`.
- Push/pull are 1:1 with desktop `sync.py` + `sync_remote_to_local…` and web
  `RemotePushService`/`RemotePullService` (same DDL, same delete columns,
  same guarded-table behavior). No scheduler anywhere: pull is first-run
  prompt + Pull button, push is the Push button (manual-only, like desktop).

## Current status

Sync is complete: Room database, remote reader/writer, `SyncRepository` and
`PullWorker` are in the repo, wired to the exact APIs the UI already calls.
Still missing (separate features, untouched by sync): `QueueRepository`,
`MetadataRepository`, `ArtworkRepository`, `SettingsRepository`,
`PodcastRepository`, the remaining read repositories the screens import, and
`DownloadService` — so the project does not compile on a fresh checkout yet.
`PLAN.md` and `FEATURE_PLAN.md` in this folder are the original build plans
kept for reference; this README describes the app itself.
