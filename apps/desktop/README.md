# Hathor — Desktop

> Part of the [Hathor monorepo](../../README.md) (`apps/desktop`). Web app: `../../web/`, mobile: `../../mobile/`.

A Windows and Linux desktop music player with a native Python backend and a modern web UI. Hathor plays your local MP3 library, downloads songs from YouTube, enriches tags and cover art from iTunes, keeps synced lyrics, and can sync your library with the Hathor server for multi-device use.

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Arch%20Linux-0078d6)
![Python](https://img.shields.io/badge/python-3.10%2B-3776ab)
![License](https://img.shields.io/badge/license-ISC-blue)

## Features

- **Playback & queue** — play/pause, next/previous, seek bar, volume with persistence, shuffle and repeat, plus a reorderable "play next" queue per song or playlist. Restarting rebuilds the queue from where you were listening (playlist, mix, artist, …). Optional equal-power **crossfade** (1–12 s) with gapless handoff.
- **Library management** — scan a local folder for MP3s, edit song metadata (title, artist, album, year, genre, cover art) in place, and delete tracks. Home is a dashboard: full-library view, daily mix, AI DJ, podcasts, and recently-played/downloaded strips. Songs can be moved to Podcasts and back without losing the file; edits to the playing file apply when the track changes.
- **Typo-tolerant search** — the shared fuzzy matcher folds diacritics and tolerates transpositions/typos across All Songs, playlists, podcasts, artists, albums and Discover.
- **Daily Mix** — a fresh 50-song mix every day built from your listening history (favorites-weighted with room for discoveries).
- **Podcasts / non-music audio** — a fully separate library (own folder, own tables) with its own view, per-episode menu, metadata editing, chapter marks with auto-skip, and two-way sync. Episodes can share the queue with songs in both directions.
- **YouTube downloads** — search YouTube in-app and download audio as 320 kbps MP3 via `yt-dlp` + FFmpeg, with live progress in the UI. A Songs/Podcast toggle routes each download to the right library (podcast downloads skip the iTunes song-match).
- **Automatic metadata enrichment** — every song download looks up the matching track on the iTunes Search API and writes ID3 tags (`TIT2`, `TPE1`, `TALB`, `TDRC`, `TCON`, `APIC`) and renames the file to the cleaned title.
- **Lyrics** — synced (LRC-style) and plain lyrics fetched per track, cached locally for offline use, with a Search/Replace panel (Web API candidates with lrclib fallback), per-song/chapter save and delete, an on-demand Romaji toggle (server romanizer with local `pykakasi` fallback), a per-song highlight-timing correction (±20 s, persisted), and a track-only fallback for artist-less songs.
- **Playlists** — create/delete playlists, add songs to multiple playlists, attach custom cover art (downscaled to ≤800 px JPEG), remove covers, and play an entire playlist.
- **History** — download history and playback (played songs / played playlists) with pagination, full-tab queues, row action menus and re-download.
- **Windows media integration** — System Media Transport Controls (SMTC) via `winsdk` for media keys and OS overlay, including album art and play/pause/next/previous.
- **Visual customization** — set a custom background image (or remove it), window size and position remembered between runs.
- **Web account login** — sign in with the same username/password as the web app. Short-lived access tokens refresh automatically; the rotating refresh token is stored in the OS credential store, never the password. Scoped `hth_` API keys remain available under Advanced.
- **In-app updates** — checks the Hathor server for a newer packaged build, verifies the SHA-256, stages it, and lets a detached helper replace the app after Hathor exits. The same Web API session powers the channel.
- **Discover** — server-backed out-of-library recommendations with source filters, YouTube preview queue, feedback/tuning and local downloads. Playback completion/skip segments are reported to the Web API for Discover quality. Configure the Web API in Settings first.
- **AI DJ (Vohithe)** — server-planned sets with spoken intro/outro commentary, voice ducking, vibe briefs (typed or spoken), batch handoff, feedback and Home/player-bar entry points.
- **Cross-device playback** — live player-hub polling keeps desktop and web/other clients aligned for play/pause/next/seek/volume, with local audio left untouched when a track is missing. Paused spots from other devices also expose their Up Next queue and resume at the saved position (explicit tap only).
- **Server sync (optional, manual)** — push/pull songs, podcasts, playlists, lyrics (with highlight offsets), podcast chapters, daily mix, and history with the Hathor server from the Settings buttons, with tombstone-based deletion propagation and first-run library import. Playback history also flushes automatically through a durable outbox; full catalog/files push remains manual.

## Tech stack

| Layer  | Technology |
| ------ | ---------- |
| Shell  | Python + [pywebview](https://pywebview.flowrl.com/) (WebView2/Edge) + `pygame.mixer` for audio |
| UI     | HTML/CSS/JS, [Tailwind CSS](https://tailwindcss.com/), [Alpine.js](https://alpinejs.dev/), [SweetAlert2](https://sweetalert2.github.io/), [Notyf](https://github.com/caroso1222/notyf) |
| Downloads | `yt-dlp`, FFmpeg |
| Metadata | `mutagen` (ID3), iTunes Search API |
| Lyrics  | `pykakasi` (romanization), `urllib` |
| Storage | SQLite (`sqlite3`); sync via the Hathor Web API (`urllib`, no DB driver) |
| Media keys | `winsdk` (Windows.Media / SMTC) |
| Packaging | PyInstaller |

## Quick start (development)

Requires **Python 3.10+**, **Node.js + npm** (for Tailwind), and **FFmpeg**.

```powershell
# 1. Python dependencies
pip install -r requirements.txt

# 2. Frontend dependencies (Alpine.js, SweetAlert2, Notyf, Tailwind CLI)
npm install

# 3. Place FFmpeg in ./ffmpeg/bin (ffmpeg.exe / ffprobe.exe)

# 4. Run
python main.py
```

On startup in dev mode, `main.py` compiles `ui/input.css` into `ui/output.css` with the Tailwind CLI automatically.

## Linux / Arch

The Linux path uses the system Python and GTK/WebKitGTK stack rather than a
vendored PyInstaller bundle. On Arch:

```bash
sudo pacman -S python python-pywebview python-gobject webkit2gtk-4.1 \
  python-pygame python-mutagen python-pillow python-psutil \
  python-pykakasi python-dotenv yt-dlp ffmpeg
python main.py
```

The app uses XDG paths on Linux:

```text
~/.local/share/hathor/music_player.db
~/.config/hathor/.env
~/.local/state/hathor/hathor.log
~/.cache/hathor/
~/Music/Hathor
~/Music/Hathor Podcasts
```

Build and install the native Arch package:

```bash
cd packaging/arch
./build-local.sh
```

See [packaging/arch/README.md](packaging/arch/README.md). The package launcher
sets `HATHOR_PACKAGE_MANAGED=1`, so yt-dlp/FFmpeg and app updates are managed
by pacman instead of the Windows self-updater. Linux uses native GTK window
decorations; the Windows frameless titlebar and Aero Snap code stays Windows-only.

## Building an executable

```powershell
pip install pyinstaller
pyinstaller music_player.spec
```

The build is emitted to `dist/Hathor/`. The `.env` file must live next to the executable (never bundled) when running the frozen build.

### Release and self-update package

`version.json` is the desktop update channel's source of truth:

```json
{
  "versionCode": 3,
  "versionName": "1.0.2-hathor"
}
```

Bump `versionCode` for every published build (the server selects the
highest), then run:

```powershell
.\scripts\release.ps1 -Destination \\DELL-SERVER1\HathorDesktop
```

The script runs the Python/JS tests, rebuilds Tailwind, builds the PyInstaller
folder, copies `version.json` next to `Hathor.exe`, and creates
`dist/hathor-desktop-<versionCode>-<versionName>.zip`. It refuses to package
`music_player.db`, `.env`, or `hathor.log`.

The server exposes the channel at:

- `GET /api/v1/desktop/latest` → metadata
- `GET /api/v1/desktop/download/{fileName}` → the zip

Both require the normal `hth_` key/JWT. **Settings → Application** shows the
installed version, checks manually, downloads with progress, and offers
**Install and restart**. The app also checks once at startup when the Web API
is configured.

Updates are staged under `%LOCALAPPDATA%\Hathor\updates` (override with
`HATHOR_UPDATE_DIR`). A detached PowerShell helper waits for Hathor to exit,
moves the old `Hathor.exe`/`_internal`/`version.json` aside, copies the staged
build, restarts Hathor, and removes the backup and package. The SQLite DB,
`.env`, logs, and library folders are never touched.

## Configuration

All settings are stored in the local `music_player.db` (SQLite) and can be changed from the in-app settings view. Server sync is opt-in: enter the server URL, username and password in **Settings → Web API** and press **Sign in**. The password is never stored; the refresh token goes to Windows Credential Manager or the Linux Secret Service. Legacy API keys are still accepted under **Advanced**.

A `.env` file next to `main.py` (or the executable) remains a fallback for the server URL and a scoped API key:

```env
HATHOR_API_URL=http://192.168.1.10:5051
HATHOR_API_KEY=hth_your-device-key
```

See [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for full details.

## Documentation

- [Installation guide](docs/INSTALLATION.md)
- [Configuration (Web API & settings)](docs/CONFIGURATION.md)
- [Usage guide](docs/USAGE.md)
- [Architecture & code overview](docs/ARCHITECTURE.md)
- [Database schema & remote sync](docs/DATABASE.md)
- [Development & packaging](docs/DEVELOPMENT.md)
- [Linux / Arch notes](docs/LINUX.md)
- [Web → desktop parity backlog](WEB_PARITY.md)

## Project layout

```
main.py            App entry point, window creation, startup + first-run flow
api.py             pywebview JS bridge: every method the UI calls
Download.py        yt-dlp YouTube downloader + iTunes metadata/artwork lookup
database.sql       SQLite schema
version.json      Desktop version/build used by the in-app updater
services/api_sync.py  Manual sync with the Hathor server (delta pull + import/file push)
services/history_sync.py  Debounced automatic Music_History outbox flush
services/app_update.py  Desktop check/download/stage/swap pipeline
services/auth_session.py  Web login, token refresh, OS-keyring refresh-token storage
services/platform_support.py  XDG paths, package-managed detection, platform capabilities
services/window_chrome.py     Isolated Windows snap/drag implementation
settings.py        Loads persisted settings from the SQLite Settings table
monitor.py         Debug RAM usage monitor (currently disabled)
services/          Domain logic: playback (+crossfade), metadata, database, lyrics, windows_media, downloads, apple
ui/                Frontend: views, modals, Tailwind/compiled CSS, vendored libs
packaging/arch/    PKGBUILD, launcher, desktop entry and icon
scripts/release.ps1  Build, package and publish a Windows desktop release
music_player.spec  PyInstaller build script (Windows; Linux uses the Arch package)
```

## License

ISC — see `package.json`. Third-party assets (`ui/sweetalert2/`, `notyf`, Alpine.js, Tailwind) remain under their respective licenses.