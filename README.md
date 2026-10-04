# Hathor

Hathor is a personal music system: local MP3 library, YouTube downloads,
metadata + lyrics enrichment, playlists, daily mixes, podcasts, and
multi-device sync. One repo, three apps, one shared sync contract.

> Repo history: this was `gabrieljdsena/Music_app` (Windows desktop app).
> It is now `gabrieljdsena/Hathor`, a monorepo holding desktop + web + mobile.
> Old `Music_app` URLs redirect automatically.

```text
apps/
  desktop/  Windows desktop player — Python + pywebview + pygame (the original app)
  web/      Self-hosted web app — ASP.NET Core 10 API + Worker + React/Vite frontend
  mobile/   Native Android app — Kotlin, on-device YouTube→MP3 + remote-DB sync
packages/
  contracts/  Sync contract shared by all three apps (schema, export/import, tombstones)
```

## The three apps

### 1. Desktop — `apps/desktop/`

The original, fully working app. Windows player with a Python backend and a
Tailwind/Alpine web UI rendered through pywebview (WebView2).

- Playback + reorderable queue, shuffle/repeat, 1–12s crossfade, SMTC media keys
- Library scan, metadata editing, Daily Mix (50 songs/day from history)
- Podcasts as a separate library, playlists with covers, history views
- YouTube search + 320kbps MP3 downloads (`yt-dlp` + FFmpeg), iTunes tags/artwork
- Synced + plain lyrics (lrclib, offline cache, romaji toggle)
- Optional manual push/pull sync to MySQL/TiDB with tombstone deletions

Stack: Python + pywebview + `pygame.mixer`, HTML/CSS/JS + Tailwind, `yt-dlp`,
`mutagen`, SQLite (`music_player.db`), PyInstaller (`dist/Hathor/`).

```powershell
cd apps/desktop
pip install -r requirements.txt
npm install
python main.py
# build: pyinstaller music_player.spec
```

Full docs: `apps/desktop/README.md` + `apps/desktop/docs/`.

### 2. Web — `apps/web/`

Self-hosted multi-user port of the desktop app. Same features and visual
system (zinc-950 + orange glass), rebuilt as a real client/server product:
any headless client (mobile app, CLI, Home Assistant) can drive playback
through the REST + SignalR API — the React UI is just one client.

- `src/Hathor.Api/` — REST (`/api/v1/...`), SignalR hubs, JWT + API keys (`hth_`), Swagger
- `src/Hathor.Domain/` + `src/Hathor.Application/` — DDD + CQRS (EF Core writes, Dapper reads)
- `src/Hathor.Infrastructure/` — EF + Dapper, yt-dlp/FFmpeg ingest, iTunes/lrclib clients
- `src/Hathor.Worker/` — Hangfire jobs (downloads, daily mix, sync-compat, maintenance)
- `frontend/hathor-web/` — React 18 + Vite + Tailwind + Zustand + React-Query + SignalR
- `tests/` — xUnit (Domain/Application/Api) + Vitest + Playwright visual parity vs desktop

```powershell
cd apps/web
docker compose up        # API + Worker + Postgres + MySQL-compat + web
# frontend dev:
cd frontend/hathor-web; npm install; npm run dev
```

Parity spec: `apps/web/IMPLEMENTATION_PLAN.md`.

### 3. Mobile — `apps/mobile/`

Native Kotlin Android app. Phase 1 proves a real on-device YouTube→MP3
download (`youtubedl-android` + bundled FFmpeg, no storage permission needed);
later phases add the sync tab (pull from the same remote MySQL/TiDB the
desktop pushes to), playlists, and full library parity.

- `app/src/main/.../engine/DownloadEngine.kt` — desktop `Download.py` semantics on-device
- `playback/PlayerManager.kt`, `PlayerService.kt` — ExoPlayer playback + queue
- `ui/` — Compose screens (Home, Library, Daily Mix, Download, Playlists, History, Now Playing)
- Sync uses the same `packages/contracts` rules (tombstones, incremental history)

```powershell
# open apps/mobile in Android Studio, let Gradle sync, Run on a phone/emulator
```

Status/plan: `apps/mobile/README.md`, `apps/mobile/PLAN.md`, `apps/mobile/FEATURE_PLAN.md`.

## Shared sync — `packages/contracts/`

All three apps stay compatible through one contract: SQLite/Room/EF schemas
mirror each other, `export`/`import` moves songs, podcasts, playlists, lyrics,
history, mixes and `Sync_Deletions` tombstones, history syncs incrementally
past remote `MAX(id)`. Change the wire shape in one app only together with the
other two in the same PR.

## Branches

- `main` — stable, always deployable. The monorepo layout (`apps/*`) lives here.
- `develop` — integration branch. Feature work branches off `develop` and merges back into it; `develop` merges to `main` for releases.
- `chore/monorepo` — historic migration branch (desktop move + web import + mobile add). Already merged into `main`; safe to delete.

```powershell
git checkout develop; git pull
git checkout -b feat/my-change
# ... work, commit ...
git push -u origin feat/my-change   # open PR against develop
```
