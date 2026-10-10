# Configuration

Hathor has two configuration surfaces:

1. **Persisted app settings** — stored in the `Settings` row of the local SQLite database and editable from the UI.
2. **Environment configuration** — a `.env` file used exclusively for the optional **MySQL/TiDB remote sync**.

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
| Queue source | `queue_source` | — | Playback-context JSON `{type, id}` rebuilt on launch |
| Custom queue | `queue_songs`, `custom_queue` | — | Manually built queue restored verbatim |

`api.py` exposes the corresponding setters to the JS UI:

- `volume_slider(volume)`
- `update_download_limit(limit)`
- `update_background(background)` / `remove_background()`
- `update_songs_path(songs_path)` / `pick_folder()`
- `update_podcasts_path(podcasts_path)` / `pick_podcasts_folder()`
- `set_crossfade(enabled, seconds)` / `get_playback_settings()`
- `pick_background()`

## Sync server (`.env`)

Sync is **disabled by default**. It is only activated when `HATHOR_API_URL` and `HATHOR_API_KEY` are present in the environment (loaded by `python-dotenv` in `main.py`). Without them, `main.py` prints:

```
[Sync] No sync server configured in .env (HATHOR_API_URL/HATHOR_API_KEY), sync disabled.
```

### Environment variables

| Variable | Required | Example | Purpose |
| -------- | -------- | ------- | ------- |
| `HATHOR_API_URL` | yes | `http://192.168.1.10:5051` | Hathor server on the LAN |
| `HATHOR_API_KEY` | yes | `hth_…` | Per-device key (`library:read` + `library:write`) |

A minimal `.env`:

```env
HATHOR_API_URL=http://192.168.1.10:5051
HATHOR_API_KEY=hth_your-device-key
```

> **Security**: `.env` is ignored by `.gitignore` — never commit real credentials.

### Where the `.env` is read

`main.py`:

1. Loads `.env` from the current working directory.
2. If the app is frozen (PyInstaller build), loads `.env` from **next to the executable** (`os.path.dirname(sys.executable)`) — it is deliberately never bundled inside the exe.

### Behavior when configured

- On **first run** with sync configured, the app prompts the user to load the server library (songs, podcasts, playlists, lyrics, daily mix, history) and download missing files into their respective folders.
- After that, everything is manual: **Push to Server** uploads rows + new file bytes; **Sync from Server** pulls the incremental delta down (see [Database & sync](DATABASE.md) and [Sync API migration](SYNC_API_MIGRATION.md)).
- Deletions are propagated via the `Sync_Deletions` tombstone table (songs, podcasts, playlists, lyrics, history).

## Turning sync off

Delete or comment out `HATHOR_API_URL`/`HATHOR_API_KEY`, or remove the `.env` file entirely. The next launch will run fully local.