# Development & Packaging

Notes for developers working on Hathor.

## Project layout

```
main.py               Entry point; window + startup, first-run server prompt
api.py                pywebview JS bridge (all methods callable from the UI)
Download.py           yt-dlp downloader + iTunes metadata/artwork
database.sql          SQLite schema
settings.py           Loads persisted settings from SQLite
monitor.py            Debug RAM monitor (disabled)
services/
  playback.py         PlaybackController — queue, shuffle, repeat, position, crossfade engine, queue-source persistence
  metadata.py         MetadataManager — mutagen tag read/write, covers, deletes (songs + podcasts), pending edits
  library_moves.py    Song ↔ podcast file/catalog moves with tombstones and rollback
  images.py           Pillow playlist-cover downscale/JPEG normalization
  database.py         DatabaseManager — SQLite CRUD, library/podcast listings, daily mix, recents, history, queue rebuild + remote import
  lyrics.py           LyricsService — fetch, cache, LRC parse, romaji, track-only fallback
  windows_media.py    WindowsMediaOverlay — winsdk SMTC media keys/overlay
  downloads.py        DownloadManager — bounded download pool, job log, retries
  discover.py         DiscoverService — feed, preview resolution, owned guard, local downloads
  ai_dj.py            AiDjController — Vohithe session, batches, voice, boundary handoff
  player_sync.py      Server player queue/play/pause/seek/volume bridge
  player_hub.py       Cross-device polling reconciliation and local publish
  telemetry.py        Playback segment observer → /player/playback-events
  api_config.py       Runtime Web API URL/key (SQLite + .env fallback)
  hathor_api.py       Shared authenticated HTTP client
  apple.py            AppleService — artist/album artwork lookups
  startup_maintenance.py  FFmpeg auto-download + yt-dlp update checks
  __init__.py         Lazy service exports
ui/
  index.html          Main single-page app shell
  views/*.html        View fragments (home dashboard, all_songs, daily_mix, discover, ai_dj, podcasts, artist, album, playlists, download, history, settings)
  js/discover-preview.js  Global Discover preview queue/dock
  js/fuzzy.js         Shared typo-tolerant search matcher
  js/chunked-list.js  200-row incremental list renderer for large directories
  js/ai-dj-*.js       Global Vohithe voice bus, vibe recorder, shell state
  assets/             Vohithe portrait and bundled UI images
  modals/*.html       Modal fragments (add/edit playlist, edit song)
  input.css           Tailwind source (v4 syntax)
  output.css          Compiled Tailwind output (generated)
  css.css             Hand-written styling
  cdn.min.js          Vendored Alpine.js
  notyf.*             Vendored Notyf
  sweetalert2/        Vendored SweetAlert2
tests/                Python stdlib/pytest tests (services, lyrics, telemetry) + JS preview/voice/fuzzy/batch smoke tests
pytest.ini            Test discovery config
requirements-dev.txt  Test tooling (pytest)
WEB_PARITY.md         Web -> desktop parity plan/progress
music_player.spec     PyInstaller spec
requirements.txt      Python deps
package.json          npm deps (Alpine, SweetAlert2, Notyf, Tailwind toolchain)
```

## Dev workflow

### Running in development

```powershell
python main.py
```

> On every dev start, Tailwind is recompiled: `npx @tailwindcss/cli -i "ui\input.css" -o "ui\output.css"`. `main.py:13-17` gates this on `not sys.frozen`.

### Editing styles

Edit **`ui/input.css`** (Tailwind v4 source) or `ui/css.css`, then restart the app or run the npx command manually. Do not edit `ui/output.css` by hand — it's generated and committed only so the frozen app needs no build step. If you change Tailwind usage, run `npm install` first to ensure the CLI is available.

### Adding a UI view

1. Add a fragment under `ui/views/` (Alpine.js-based, matching the existing pattern).
2. Wire it into `ui/index.html` and its JS handlers.
3. Add the bridge method to `API` in `api.py` (and service logic under `services/` if needed).
4. Recompile Tailwind if you used new utility classes.

### Adding a Python service

- Follow the existing services pattern: define a class that takes `api` as its constructor arg so it can read shared state (`api.playing`, `api.song_list`, ...) and push to the UI via `api._window.evaluate_js(...)`.
- Export it from `services/__init__.py`.
- Keep `api.py` thin: delegate to the service and return its result.
- New views that play audio must pass a queue `source` (`{type, id}`) to `populate_queue_from_list` so restarts rebuild the right queue; new tables need a `CREATE TABLE IF NOT EXISTS` in both `database.sql` and `Api._ensure_schema`, plus a snapshot section in the sync contract (`packages/contracts/README.md`) if they should sync.

## Building the executable (PyInstaller)

```
pip install pyinstaller
pyinstaller music_player.spec
```

Output: `dist/Hathor/`.

The spec bundles `ui/`, `ffmpeg/`, `database.sql`, `version.json`, and `pykakasi` data files. Runtime frontend assets (SweetAlert2, Notyf, Alpine) live under `ui/`; `node_modules/` is only needed for the Tailwind dev build. Important details:

- **`.env` is not bundled** — it must sit next to `Hathor.exe` (runtime reads `os.path.dirname(sys.executable)`).
- **Refresh tokens** live in OS credential storage through `keyring`; PyInstaller collects its backend modules explicitly. Passwords are never stored.
- The SQLite DB, `.env`, and library folder all live in `get_data_path()` (`sys.executable` dir) when frozen.
- Console is disabled (`console=False`), so stdout logs are invisible in the shipped app.
- `version.json` is copied to the app root for the updater; the PyInstaller copy under `_internal` is only a fallback.

> `dist/` and `build/` are git-ignored.

### Publishing an update

Bump `versionCode` in `version.json`, then run:

```powershell
.\scripts\release.ps1
```

`-SkipTests` and `-SkipTailwind` speed up local builds. The script emits
`dist/hathor-desktop-<versionCode>-<versionName>.zip`; push a
`desktop-v<versionName>` tag so the GitHub workflow publishes it. Current
clients read the public GitHub release feed, verify the asset digest, and stage
updates under `%LOCALAPPDATA%\Hathor\updates` using `services/app_update.py`
plus a detached PowerShell helper for the swap. The old server endpoint remains
for builds older than `1.0.3`. Never edit a published zip or reuse a
`versionCode`.

## Linux / Arch package

The Linux build is a normal Arch package using system Python, GTK 3 and
WebKitGTK 4.1; PyInstaller is not used. Build it inside Arch:

```bash
cd packaging/arch
./build-local.sh
```

`build-local.sh` packages the current `apps/desktop` tree and runs
`makepkg -si`. The installed launcher sets `HATHOR_PACKAGE_MANAGED=1`, which
disables pip/self-update behavior and selects XDG data/config/cache/state
paths. Runtime files go to `/usr/lib/hathor`; user data never does.

### Linux release installer

`scripts/install-linux.sh` is the per-user release installer/updater. It is
bundled into the Linux zip by `scripts/package-linux.sh` and also published as
a standalone release asset. It installs under `~/.local/opt/hathor`, writes a
launcher + desktop entry, prefers the system Python/GTK/WebKitGTK stack, and
falls back to a `--system-site-packages` venv for pip-only modules. Script
installs set `HATHOR_INSTALLER_MANAGED=1` (plus `HATHOR_PACKAGE_MANAGED=1` for
older builds), so `main.py` skips the Tailwind `npx` step and the Settings
update card points at `install-linux.sh --update`.

Build the zip locally with `bash scripts/package-linux.sh`; it emits
`dist/hathor-linux-<versionCode>-<versionName>.zip`.

PyInstaller cannot cross-compile from Windows, and bundling WebKitGTK is not
the supported Linux path. Use an Arch VM/container for package builds.

## Code conventions

- **No external comments added to code** — existing files keep their brief `# Region` separators and inline notes but new code follows the quiet style of the codebase.
- Logging to stdout uses `[Python]`, `[Sync]`, `[Debug]` prefixes.
- JSON is passed between Python and JS as serialized strings via `evaluate_js`; bridge methods that receive data from JS accept plain dicts from `pywebview`.

## Testing

```powershell
python -m pytest -q
python -m unittest discover -s tests
npm run test:js
```

The Python suite is stdlib-compatible, so it runs without pygame/pywebview. The
JS smoke test uses minimal DOM/Audio fakes for the global Discover preview
queue. `.github/workflows/desktop-tests.yml` runs both suites, `npm ci` and the
Tailwind build on Windows.

## Contributing flow

1. Create a branch / fork.
2. Make changes, keeping the service-wrapper pattern in `api.py`.
3. Run the automated tests above.
4. Run the app once with the new code; sanity-check downloads, playback, and (if configured) sync/Discover.
5. Commit with a concise message referencing the feature/version (repo history uses version tags like `0.90`).