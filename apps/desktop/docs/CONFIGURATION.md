# Configuration

Hathor has one configuration surface: **persisted app settings**, stored in the
`Settings` row of the local SQLite database and edited from the UI. The sync
server is entered only on the dedicated login screen.

## App settings (SQLite `Settings` table)

Loaded at startup by `settings.py` and kept current by `api.py` whenever you change something in the UI.

| Setting | Column | Default | Notes |
| ------- | ------ | ------- | ----- |
| Songs folder | `songs_path` | `%appdata%\musicPlayer` | Folder scanned for `.mp3` files |
| Podcasts folder | `podcasts_path` | `%appdata%\musicPlayerPodcasts` | Separate non-music library |
| Volume | `current_volume` | `0.7` | Applied to `pygame.mixer` on every change |
| Window size | `window_width`, `window_height` | `1280 × 720` | Saved 0.4 s after each resize |
| Download limit | `limit_downloads` | `3` | Max concurrent downloads |
| Background | `background_path` | *(empty)* | Custom background image path |
| Crossfade | `crossfade_enabled`, `crossfade_seconds` | off, `5.0` | 1–12 s equal-power overlap on auto-advance |
| Current song | `current_song` | — | Resumed on next launch |
| Current playlist | `current_playlist` | — | Playlist resumed on next launch (legacy; superseded by `queue_source`) |
| Queue source | `queue_source` | – | Playback-context JSON `{type, id}` rebuilt on launch |
| Custom queue | `queue_songs`, `custom_queue` | – | Manually built queue restored verbatim |
| Web API URL | `hathor_api_url` | *(empty)* | Server root saved by the login screen |
| Web API key | `hathor_api_key` | *(empty)* | Scoped `hth_` token; masked in the UI |

`api.py` exposes the corresponding setters to the JS UI:

- `volume_slider(volume)`
- `update_download_limit(limit)`
- `update_background(background)` / `remove_background()`
- `update_songs_path(songs_path)` / `pick_folder()`
- `update_podcasts_path(podcasts_path)` / `pick_podcasts_folder()`
- `set_crossfade(enabled, seconds)` / `get_playback_settings()`
- `pick_background()`

## Sync server (dedicated login screen)

Sync is **disabled by default**. Point the app at a server on the dedicated
login screen:

1. Enter the server root (for example `http://192.168.1.10:5051`).
2. Enter your username and password.
3. Press **Sign in**. Access tokens rotate automatically; the refresh token is kept in the OS credential store and the password is never stored.

A scoped `hth_` API key remains available under **Advanced** on the same login
screen for automation and older installs. **Settings → Web API** shows the
current URL/status and offers Test, Sign out, and Clear. The server URL, auth
mode and username are stored in the `Settings` row; the refresh token lives in
Windows Credential Manager or the Linux Secret Service. Environment variables
and `.env` files are ignored: a machine only talks to the server the user
signed in to. Without a saved session, `main.py` prints:

```
[Sync] No sync server configured; sign in from the login screen.
```

### Runtime fields

| Field | Required | Example | Purpose |
| ----- | -------- | ------- | ------- |
| `hathor_api_url` | yes | `http://192.168.1.10:5051` | Hathor server on the LAN |
| `hathor_auth_mode` | no | `login` / `key` | Runtime sign-in mode |
| `hathor_auth_username` | no | `gabriel` | Web user shown in Settings |
| `hathor_api_key` | no | `hth_...` | Legacy/advanced scoped key |

The password never leaves the login request and is never persisted. The full API
key stays in Python/SQLite; Settings only receives a masked hint.

### Behavior when configured

- On **first run** with sync configured, the app prompts the user to load the server library (songs, podcasts, playlists, lyrics, daily mix, history) and download missing files into their respective folders.
- After that, everything is manual: **Push to Server** uploads rows + new file bytes; **Sync from Server** pulls the incremental delta down (see [Database & sync](DATABASE.md) and [Sync API migration](SYNC_API_MIGRATION.md)).
- Deletions are propagated via the `Sync_Deletions` tombstone table (songs, podcasts, playlists, lyrics, history, podcast tags and podcast tag links).

## Desktop updates

**Settings → Application** shows the installed `version.json`, checks the
public GitHub release feed (`desktop-v*`, dev channel), downloads with progress,
verifies the asset digest/`SHA256SUMS.txt`, and offers **Install and restart**.
The app also checks once at startup. This is independent of the Web API URL/key
and needs no `hth_` login. The old `/api/v1/desktop/latest` endpoint remains for
builds older than `1.0.3`.

On Arch/Linux package installs (`HATHOR_PACKAGE_MANAGED=1` or a
`.package-managed` marker in the app directory) the card instead reports that
updates are managed by the system package manager. Use `pacman -Syu`; the
Windows zip/PowerShell updater is never used against `/usr/lib/hathor`.

Installations made by the release installer (`install-linux.sh`, env
`HATHOR_INSTALLER_MANAGED=1` or a `.installer-managed` marker) take precedence:
the card reports that the installer owns updates and points at
`~/.local/opt/hathor/install-linux.sh --update`. Check, download and install
buttons stay hidden; user data is untouched by updates.

Downloaded packages and the staged copy live under
`%LOCALAPPDATA%\Hathor\updates`. Set `HATHOR_UPDATE_DIR` to use another folder.
The installer helper waits for Hathor to close, swaps `Hathor.exe`,
`_internal`, and `version.json`, then restarts the app; `music_player.db`,
logs, and library folders are preserved.

## Turning sync off

Press **Clear** in Settings → Web API (or **Sign out**). The next launch runs fully local until you sign in again.