# Hathor Web — Implementation Plan (C# + React + DDD + Dapper)

> **Superseded in part (Oct 2026):** the shared MySQL/TiDB remote is retired.
> Devices sync over the Web API now — `GET /api/v1/sync/delta?cursor=`,
> `POST /api/v1/sync/import` (→ `missingFiles`), `GET/PUT
> /api/v1/sync/files/{file}` (see `packages/contracts/README.md`); the
> per-library pull-/push- endpoints, `RemotePull/PushService`, and the
> compose `db` service are deleted. The server is Postgres-only. Plan items
> below that describe the remote DB are historical — everything else stands.

**Stack:** C# ASP.NET Core 10 backend (DDD + REST + EF Core + Dapper) + React 18 + Vite + Tailwind frontend.
**App name:** Hathor (matches desktop + mobile).
**Sources of truth:**

- Desktop: `apps/desktop/` — `api.py`, `services/` (`playback`, `metadata`, `database`, `lyrics`, `apple`, `downloads`, `windows_media`, `startup_maintenance`), `Download.py`, `sync.py`, `settings.py`, `database.sql`, `ui/index.html` + `ui/views/*` + `ui/modals/*`, `docs/*.md`
- Mobile: `apps/mobile/` — `PLAN.md`, `FEATURE_PLAN.md`, `AppDatabase.kt` (Room), `QueueRepository.kt`, Compose screens, `PlayerService/PlayerManager`, `SyncWorker/PullWorker`, `RemoteConn/RemoteSync/RemoteUpload`

---

## 1. Unified Feature Inventory (parity target)

1. **Library:** scan/import MP3 folder, `get_all_songs`, `send_song_list`, `get_library_count`, edit metadata (title/artist/album/year/genre/cover), delete w/ tombstone. Home dashboard: All Songs card + Daily Mix card + Recently Played / Recently Downloaded strips with live counts.
2. **Playback & Queue:** play/pause/next/prev/seek (`get_current_pos`, `progress_slider_click`), persisted volume, shuffle/repeat, `populate_queue[_from_list]`, `add_to_queue` / `next_to_queue`, `remove/reorder/clear`, `jump_to_queue_index`, restart rebuild from `queue_source` (`playlist|daily_mix|artist|album|recently_played|recently_downloaded|podcast|all_songs`), persisted `current_song` + `queue_songs` + `custom_queue` + `is_custom_queue` + `unshuffled_song_list`. Crossfade 1–12s equal-power + always-on gapless handoff. Web: backend owns state, `<audio>`/WebAudio + Media Session API replaces `pygame` + SMTC / Android `PlayerService` + `MediaSession`.
3. **Daily Mix:** fresh 50/day from history. Exact quotas: 2× top 10, 5× ranks 11–25, 13× ranks 26–70 by `Music_History` counts, rest prefer outside top 70 (discovery), `min(50, n)`, shuffle at end, `DELETE` non-today rows, `{date, songs, cached}`. Small libraries get fewer songs.
4. **Podcasts (separate library):** own storage path/tables, lightweight listing (no duration/cover until details/play), `get_podcasts/details`, `delete_podcast`, metadata edit, tags (`Podcast_Tags` / `Tag_Links`: CRUD + assign/unassign, duplicate/blank guards, live `episode_count`, filter chips), bento ⋮ menu (Play, Play Next, Add to Queue, Get Metadata, Edit Info, Tags…, Delete), shared queue with songs both directions, excluded from music history/mix/recents.
5. **YouTube ingest:** `search_yt` (ytsearch5, flat, no download), `recieve_download/submit {url,title,artist,is_podcast}`, `get_download_jobs`, `retry_download`, `cancel`, concurrency limit (`limit_downloads`), Songs/Podcast toggle, live progress stages (0→90 download, 90 finished, 95 processing_metadata, 100 done), 320kbps MP3 via yt-dlp + FFmpeg, `outtmpl %(id)s_%(title)s`, `.txt` batch import (one query/line, 1.5s throttle, respects toggle).
6. **Enrichment:** iTunes Search API single + multi (`search_itunes[_multi]`), ID3 `TIT2/TPE1/TALB/TDRC/TCON/APIC` (ID3v2.3), safe-rename to cleaned title with `Title (1).mp3` collision handling, `get_artist_image/get_album_image` (600×600, exact-match preference), iTunes RSS trending `topsongs/limit=4/json` + quick-search tags, YouTube preview iframe modal.
7. **Lyrics:** lrclib get/search/save per `song_file` (JSON `{synced, plain}`), offline cache (`Lyrics` table, cache-first), cleaning (`split " - "`, strip `(remaster|mix|live|feat)`, `?/?` normalize), `/api/get?track&artist&duration`, track-only exact case-insensitive non-instrumental fallback for artist-less songs, `/api/search` max 10 deduped suggestions, romanization toggle (pykakasi Hepburn preserving `[mm:ss.xx]` → server `POST /lyrics/romanize`).
8. **Playlists:** CRUD + custom cover (`thumbnail` BLOB → TEXT base64), `get_playlist_songs`, `get/update_song_playlists` (multi-assign, `INSERT OR IGNORE Songs` first), play-all / shuffle-play, submenu + grid counts (Dapper), edit/delete with tombstones, `slimPlaylist` (no thumbnail in DOM for RAM).
9. **History:** `Music_History` + `Playlist_History` + `Download_Queue` with pagination, `get_recently_played/downloaded` strips, per-instance rows (`_historyId = field_file_date`, same file replayable, ring highlight, artist-link navigation, Re-download button only when `DownloadedLink` present), Download Jobs panel (2.5s poll, retry).
10. **Settings/Personalization:** volume, download limit (UI 1–10, backend clamp 1–20), background image upload/remove + live apply, crossfade prefs + switch/slider UI, songs/podcasts rescan buttons, `current_tab` → `lastRoute`, `browser` → server yt-dlp cookies option, `standardize_volume` reserved. Desktop window size/position dropped (responsive web). Android SAF folder picker → per-user storage quota UI; system volume → per-user volume.
11. **Sync-compat:** manual push/pull to MySQL/TiDB (same `REMOTE_SCHEMA` in `sync.py`), tombstone `Sync_Deletions` propagation, first-run import prompt. Web is natively multi-user; keep `export/import` for desktop/mobile compat + `SyncWorker/PullWorker` semantics via Hangfire.
12. **Maintenance:** FFmpeg resolve (PATH → bundled → gyan.dev essentials ~85MB) + yt-dlp PyPI check + frozen-skip + `{stage,label,percent,detail,status}` toast + Retry. Web: bake ffmpeg into Docker + `/system/*` health endpoints + Hangfire self-update check.
13. **Observability:** Serilog (console + logs/hathor-.log + shared lab Postgres `logs` table, application='hathor', for Error/Fatal; credentials in gitignored appsettings.Secrets.json) + `POST /logs/client` + correlation IDs + X-Request-Id + global ExceptionHandlingMiddleware (unhandled errors logged, problem+json, no leaked internals)
14. **Visual system (from Android `FEATURE_PLAN.md`):** `bg-zinc-950 #09090B`, glass `bg-black/40 blur-2xl border-white/5`, accent `orange-500 #F97316 / orange-400 #FB923C / orange-600 #EA580C`, `HeaderIconTile`, orange confirm dialogs, dark snackbars, ambient orange glows, 4-bar `visualizer-bounce`. Views: `home, all_songs, artist, album, daily_mix, podcasts, download_songs, playlist_home/view, history, settings` + modals `add_playlist, edit_song` + bottom player + queue sheet + lyrics overlay + fullscreen Now Playing.

---

## 2. Architecture (DDD + CQRS + EF Core + Dapper)

```text
React (Tailwind, Zustand + React-Query, SignalR) ──HTTPS──> ASP.NET Core API
                                                              │
          ┌───────────────┬──────────────┬────────────────────┴───────────────┐
   Domain / Application   Infrastructure  Background (Hangfire)  Realtime (SignalR)
   (aggregates, CQRS)     (EF + Dapper)   (downloads, mix, sync)  (progress, queue)
```

### Solution layout (`Hathor.sln`)

```text
src/Hathor.Domain/                  Aggregates, Entities, VOs, DomainEvents, IDomainServices, IRepositories
src/Hathor.Application/             CQRS (MediatR), DTOs, Validators (FluentValidation), Ports
  Commands/                         Write use-cases (EF Core)
  Queries/                          Read use-cases (Dapper)
src/Hathor.Infrastructure/
  Ef/                               HathorDbContext, EF repositories (commands), migrations, Identity
  Dapper/                           DapperConnectionFactory, SqlQueries, Dapper repositories (queries)
  Storage/                          File/S3 storage (per-user), FileNamingService, TagLibSharp metadata
  Ingest/                           YtDlpService, FFmpegService
  Enrichment/                       ITunesClient, LrclibClient, RomanizationService
  Auth/                             Identity + JWT
  Jobs/                             Hangfire jobs
  Logging/                          Serilog
src/Hathor.Api/                     Controllers, SignalR Hubs, Middleware, Swagger, Versioning (/api/v1)
src/Hathor.Worker/                  Hangfire host (downloads, daily-mix, sync-compat, maintenance)
frontend/hathor-web/                React + Vite + TS + Tailwind + shadcn (visual clone of `Music Player/ui`, see §5.1)
tests/Hathor.Domain.Tests/          Pure unit tests (no I/O — quotas, queue rules, filename, lyrics cleaning)
tests/Hathor.Application.Tests/     Handler tests (MediatR + Moq/NSubstitute, in-memory EF + fake Dapper)
tests/Hathor.Infrastructure.Tests/  Dapper query tests (Testcontainers MySQL) + TagLibSharp metadata tests
tests/Hathor.Api.Tests/             Controller + SignalR hub tests (WebApplicationFactory, JWT stub)
tests/hathor-web/                   Vitest + Testing Library (components) + Playwright (visual/journeys)
docker-compose.yml                  API + Worker + Postgres (localdb) + MySQL/TiDB (remote-compat) + storage + ffmpeg
```

### Bounded contexts / Aggregates

| Context | Aggregate root | Key invariants |
|---|---|---|
| Library | `Song(file)`, `Podcast(file)` | safe-rename unique (`_safe_filename`, 150 chars, `Title (n).mp3`), ID3 write atomic, genre/year preserved |
| Playlists | `Playlist(id)` + `SongPlaylist`, `PodcastTag` + `TagLink` | no dup links (`idx_podcast_tag_links_pair`), counts = live files only |
| Playback | `PlaybackState(userId)` + `Queue` | `queue_source {type,id}` rebuild rule from `Api._rebuild_queue_from_source`; `restore_saved_queue` verbatim wins; `first_play/opening`, `instanceId` replay restarts |
| Ingestion | `DownloadJob(qid)` | status `queued\|downloading\|processing\|completed\|failed\|cancelled`, concurrency cap 1–20, dedupe in-flight URLs, stuck watchdog 5 min, `is_podcast` routing preserved on retry |
| Lyrics/Metadata | `Lyric(song_file)` | one cached row per song (`{synced,plain}`), lrclib upstream, exact-match fallback only |
| History/Recommend | `PlayEvent`, `DailyMix(mix_date)` | append-only, `MAX(id)` incremental sync, quotas 2/5/13/rest, adopt-remote-mix + prune `<today` |
| Identity/Settings | `User`, `UserSettings` | volume, crossfade 0–12s, background, limit, `lastRoute`, per-user storage isolation |

Domain events: `SongDownloaded`, `MetadataEnriched`, `SongDeleted (→tombstone)`, `PlaylistUpdated`, `SongPlayed (→history + mix invalidation, podcasts excluded)`.

### Dapper vs EF Core rules

- **EF Core (write model):** migrations, Identity, `DbContext`, Aggregates, `Add/Update/Delete`, tombstones, `Playlist.thumbnail TEXT`, `Sync_Deletions`. Single source for schema.
- **Migrations (per-provider sets):** `src/Hathor.Migrations.Postgres` (local dev database) + `src/Hathor.Migrations.MySql` (remote-compat/TiDB) (`InitialCreate` in each; identity annotations differ so one set cannot serve both). Runtime picks via `MigrationsAssembly` in `ServiceExtensions`. Startup runs `EnsureMigrated()`: legacy EnsureCreated-era DBs (tables, no history rows) get missing tables created + `InitialCreate` stamped, poisoned DBs (stamped but tables missing) get repaired, then `Migrate()`. New schema change = add migration to **both** sets: `dotnet ef migrations add <Name> --project src/Hathor.Migrations.Postgres --startup-project src/Hathor.Migrations.Postgres` then the same for `MySql` (design-time factories live in each project; no live DB needed).
- **Dapper (read model):** all hot/list queries — no change tracking, raw SQL, `snake_case` mapping. This is where desktop `sqlite3` + Android Room `DAO` queries go.
- **Commands** (`POST/PATCH/DELETE`, Hangfire ingest, sync import): EF Core + Domain events.
- **Queries** (`GET` lists, counts, history pages, mix ranking, search): Dapper `QueryAsync<T>` with `LIMIT/OFFSET`, `COUNT(*)`, `GROUP BY song_file` for `_get_ranked_files` (`services/database.py:517`), `MAX(id)` for incremental history (`sync.py:329`).
- **Bulk compat** (`executemany` upserts, full-replace `song_playlist`, `INSERT IGNORE` history): Dapper `ExecuteAsync`.
- **Mapping:** Dapper multi-map `SongRow → SongDto + DateDownload ISO8601` (`...T...Z` convention), progress `0..100 → 0..1` heal (`QueueRepository.kt:404`).

Phase 1 Dapper tasks: `DapperConnectionFactory (MySQL/TiDB)`, `SongQueries`, `PlaylistQueries (with-counts)`, `HistoryQueries`, `MixRankingQuery`, `TagCountQuery (live-files only)`.

---

## 3. Data Model (EF Core, port of `database.sql` + lowercase remote)

`Songs, Podcasts, Playlists, Song_Playlist, Podcast_Tags, Podcast_Tag_Links, Download_Queue (qid UNIQUE, is_podcast)`, `Lyrics (song_file, lyrics JSON)`, `Music_History, Playlist_History, Sync_Deletions, Daily_Mix (mix_date PK, song_files JSON)`, `Users, UserSettings` + `AspNetIdentity`.

- Add `UserId` FK to all user-scoped tables (multi-user delta vs desktop single-user SQLite).
- `Playlists.thumbnail`: `TEXT` (base64/url) — `sync.py` normalizes BLOB → UTF-8 text for remote.
- Binaries on disk/S3 (`/storage/{userId}/songs|podcasts|covers|backgrounds/`), DB stores `file` key only — enables byte-range streaming.
- Keep `genre` column/ID3 field even though `edit_song.html` only shows Title/Artist/Album/Year + Cover (desktop `update_song_metadata` drops genre; `Download.apply_metadata` writes `TCON` — preserve it).
- Keep `downloaded_link` (YouTube source, drives Re-download), `date_download`, `date_played/date_added` ISO8601 `...T...Z`.
- `Settings` single-row (id=1) → `UserSettings`: `current_song, current_playlist, current_volume, limit_downloads, background_path, songs_path→quota, podcasts_path→quota, queue_songs, custom_queue, queue_source JSON, crossfade_enabled/seconds, lastRoute (ex-current_tab), browser (yt-dlp cookies), standardize_volume (reserved)`.

---

## 4. Full RESTful API (v1, JWT Bearer + API keys, `/api/v1/...`, Swagger + versioning)

> External-first: every endpoint below (especially song controls in Playback) must work headless outside `hathor-web` — curl, mobile app, CLI, Home Assistant, Stream Deck. The web frontend is just one API client; no `pywebview`-style private bridge. Publish OpenAPI (`/swagger/v1/swagger.json`), CORS allowlist, per-user rate limiting, and `X-Request-Id` correlation on all responses.

### Auth / Users (+ external API keys)
- `POST /auth/register | /auth/login | /auth/refresh | /auth/logout` (rememberMe flag → 24h session default, 30d when marked; DeviceLabel from User-Agent)
- `POST /auth/logout-all` + `GET /auth/sessions` / `DELETE /auth/sessions/{id}` (multi-login: server-side `Sessions` table, rotation with reuse detection burns all sessions on replay, per-session revoke; access tokens stay 15min with silent refresh)
- `GET /auth/status {registrationOpen}` (single-account mode: register 409s once any user exists)
- `GET|POST /api-keys` / `DELETE /api-keys/{id}` (personal access tokens for headless clients; `Authorization: Bearer <pat>`, scopes `player:read player:control library:read`, hashed storage, prefix `hth_`, per-key rate limit + revoke; JWT stays for browser sessions)
- Granular write scopes for least-privilege integrations: `downloads:write` (submit/retry/cancel + poll jobs), `playlists:write` (playlists/tags CRUD + song links + list), `library:write` (metadata, delete, scans, sync import/pull/push, settings, mix regenerate + library reads). `player:control` stays the full superset; every action carries exactly one policy (stacked `[Authorize]` attributes AND-combine). Key management itself needs `player:control` so scoped keys can't mint escalations.
- Hardening: BCrypt password hashes (never reversible), JWT key ≥32 bytes enforced at startup, HS256-only + issuer/audience/lifetime + zero skew (mirrored in `?token=` validation), Auth 5/min rate limit

### Songs library
- `GET /songs?search=&artist=&album=&sort=Title|Album|Duration|DateDownload&dir=&page=&pageSize=` (Dapper; sort keys match `all_songs.html` headers; typo-tolerant fuzzy search across title/artist/album)
- `GET /songs/count`
- `GET /songs/with-counts` (frontend compat helper, optional)
- `GET /songs/{file}?includeCover=true` (lazy cover: `_coverCache` + `get_cover_art_base64` semantics)
- `PATCH /songs/{file} {title,artist,album,year,genre,cover|REMOVE}` (returns `resumeSec` if currently playing — client seeks after reload)
- `DELETE /songs/{file}` (deletes `Song_Playlist+Lyrics+Music_History+Songs`, tombstones `songs/lyrics/music_history`, evicts from queue, stops if current, SignalR `QueueUpdated`)
- `GET /songs/{file}/stream` (206 Partial Content range streaming)
- `GET /songs/{file}/cover` / `POST /songs/{file}/cover {dataUrl|httpUrl}` / `DELETE /songs/{file}/cover`
- `POST /songs/scan` (replaces `sync_local_songs_to_db`)
- `GET /songs/recently-played?limit=15` / `GET /songs/recently-downloaded?limit=15` (Dapper, unique + newest-first, files-must-exist)

### Artists / Albums
- `GET /artists` / `GET /artists/{name}/songs` (exact metadata match, sort Album+Title — `database.py:238`)
- `GET /albums` / `GET /albums/{title}/songs?artist=` (exact match, sort Artist+Title)
- `GET /artists/{name}/image` / `GET /albums/{title}/image?artist=` (iTunes, exact-match preference, 600×600)

### Podcasts
- `GET /podcasts` (lightweight: file/title/artist/date, newest-first, no duration/cover — `database.py:400`)
- `GET /podcasts/{file}` (full via TagLibSharp + cover — `get_podcast_details`)
- `PATCH /podcasts/{file}` / `DELETE /podcasts/{file}` (cleans `Tag_Links+Podcasts`, tombstone `podcasts`)
- `GET /podcasts/{file}/stream`
- `POST /podcasts/scan` (replaces `sync_local_podcasts_to_db`)
- Tags: `GET|POST /podcast-tags`, `GET /podcast-tags/with-counts` (live-files only), `PUT|DELETE /podcast-tags/{id}` (blank/duplicate guards → 409, `new_podcast_tag→-1` / `rename→False` semantics), `GET /podcasts/{file}/tags`, `PUT /podcasts/{file}/tags` (bulk), `POST /podcasts/{file}/tags/{tagId}`, `DELETE /podcasts/{file}/tags/{tagId}`

### Playlists
- `GET|POST /playlists` / `GET|PUT|DELETE /playlists/{id}` (delete cleans links+history + tombstones)
- `GET /playlists/with-counts` (fixes `playlist_home.html:175` hardcoded `0 songs` + sidebar submenu)
- `GET /playlists/{id}/songs` (with `DateAdded ...Z`)
- `POST|DELETE /playlists/{id}/songs` + `PUT /playlists/{id}/songs` (bulk reorder)
- `GET /songs/{file}/playlists` / `PUT /songs/{file}/playlists` (multi-assign, ensure-Song-first)
- `GET /playlists/{id}/cover` / `POST /playlists/{id}/cover` (replaces `pick_playlist_image`)

### Playback / Queue / Player state (headless song controls — same surface for web + external clients)
- `GET /player/state {current_song,is_playing,pos,volume,shuffle,repeat,queue,source,firstPlay}` — pollable by any client; `pos` is server-estimated (`last_play_time + offset`) so headless remotes stay in sync without audio
- `POST /player/play {file,instanceId,opening}` / `POST /player/pause|/player/next|/player/prev` — idempotent, return full `state`; `next/prev` work with empty body (advance current queue); `play` with no body toggles like desktop `play_button(null)`
- `POST /player/toggle` (explicit play/pause flip for remotes/buttons that can't track state; returns `is_playing`)
- `POST /player/seek {sec}` (replaces `progress_slider_click`) / `POST /player/seek-by {deltaSec}` (±10s remote/media-key step, clamped to duration)
- `POST /player/volume {0..1}` / `POST /player/mute` + `POST /player/unmute` (server keeps `_previousVolume` like `index.html:588`) / `POST /player/shuffle` / `POST /player/repeat`
- `GET|PUT /player/queue` (full replace = `populate_queue_from_list {song,list,playlistId,source}`)
- `POST /player/queue/add` (=`add_to_queue`) / `POST /player/queue/play-next` (=`next_to_queue`)
- `DELETE /player/queue/clear` / `DELETE /player/queue/{index}` / `PUT /player/queue/reorder {old,new}` / `POST /player/queue/jump {index}` (slice `[index+1:]`)
- `GET|PUT /player/settings {crossfade_enabled, crossfade_seconds 1..12}` (replaces `get_playback_settings/set_crossfade`)
- External-client rules: all controls are per-user `PlaybackState` mutations (backend is source of truth, audio renders wherever that user is playing — web `<audio>` today, server/headless renderer later); every mutating control broadcasts SignalR `PlaybackStateChanged` + `QueueUpdated` so web UI and external remotes converge; include `Idempotency-Key` header support on `play/next/prev/seek` for flaky remote buttons.

### External control surface (beyond the web UI)
- Transports: REST (above) for commands + `GET /player/state` polling, SignalR `/hubs/player` (`PlaybackStateChanged`, `QueueUpdated`) for push, Media-Session-compatible `GET /player/now-playing {title,artist,cover_url,position,duration}` for widgets/integrations.
- Discoverability: OpenAPI tags `Player`, `Queue`, `Library`; `GET /api/info {version,server_time,features:[crossfade,lyrics,romanize]}` for capability checks by third-party clients.
- Safety: `player:control` scope required for mutating controls; read-only tokens get `player:read` (state/queue/now-playing only); document curl examples in Swagger (`curl -H "Authorization: Bearer hth_..." POST /api/v1/player/next`).

### Downloads / YouTube
- `GET /youtube/search?q=&limit=5` (flat, no download — `search_yt`)
- `POST /downloads {url|query,title,artist,isPodcast}` → 202 + `qid`
- `POST /downloads/batch (multipart .txt + isPodcast)` (one query/line, concurrency = limit, 1.5s throttle)
- `GET /downloads?limit=&status=` / `GET /downloads/{qid}` / `GET /downloads/active`
- `POST /downloads/{qid}/retry` (preserves `is_podcast`) / `POST /downloads/redownload {url,title,artist}`
- `DELETE /downloads/{qid}` (queued→cancelled instant, downloading→cancel yt-dlp process) / `POST /downloads/clear-completed`

### Metadata
- `GET /metadata/itunes?title=&artist=` (single, query-cleaning fallbacks)
- `GET /metadata/itunes/options?title=&artist=&limit=5`
- `GET /metadata/trending` (proxy iTunes RSS `topsongs/limit=4/json`)

### Lyrics
- `GET /lyrics?track=&artist=&album=&duration=` (cache-first, `/api/get`, track-only exact fallback)
- `POST /lyrics/search` (max 10 deduped suggestions, skip instrumentals)
- `GET|PUT /songs/{file}/lyrics {synced,plain}` (offline cache)
- `POST /lyrics/romanize {text,isLrc}` (Hepburn, preserve `[mm:ss.xx]`)

### History / Mix
- `GET /history/downloads?page=&pageSize=` (Dapper, `downloaded_link` non-empty, files-must-exist, `{items,total_pages,current_page}`)
- `GET /history/played-songs?page=&pageSize=` / `GET /history/played-playlists?page=&pageSize=`
- `POST /history/played-songs {file,instanceId}` / `POST /history/played-playlists {playlistId}` (logged on `play_button`; podcasts excluded)
- `GET /daily-mix` (`{date,songs,cached}`) / `POST /daily-mix/regenerate`

### Settings / System
- `GET|PUT /settings {volume,limit,crossfade,background,lastRoute,browser}` (limit UI 1–10, backend clamp 1–20)
- `POST /settings/background (upload)` / `DELETE /settings/background`
- `GET /system/ffmpeg` / `GET /system/libraries` / `POST /system/maintenance/run` (SignalR progress)

### Sync-compat (desktop/mobile)
- `GET /sync/export?sinceId=` (songs/podcasts/playlists/links/lyrics/history/mix/deletions)
- `POST /sync/import` (upsert + tombstones, guarded tables for old remote DBs, `MAX(id)` incremental history, `AUTO_INCREMENT` align, mix prune `<newest-local`, pull adopts remote mix + prunes `<today`)
- `POST /sync/pull-songs` / `POST /sync/pull-podcasts` (desktop `sync_remote_to_local_and_download` split per library: read desktop TiDB remote via `DB_HOST`/`DB_PORT`/`DB_USER`/`DB_PASSWORD`/`DB_NAME` env or `RemoteDb` config section, merge through `/sync/import` semantics, queue downloads pinned to the exact remote filenames via `Download_Queue.TargetFile` so merged rows resolve on disk; Settings rows carry Sync + Pull buttons, JSON file import removed)
- `POST /sync/push-songs` / `POST /sync/push-podcasts` (desktop `DatabaseSync` push split per library: schema init + `ON DUPLICATE KEY UPDATE` upserts, link tables replaced scoped to this user's playlists/tags, history incremental past remote `MAX(id)`, `AUTO_INCREMENT` align, daily-mix newest-wins prune, deletion tombstones propagated; Settings rows carry Push buttons)

### Realtime (SignalR `/hubs/downloads`, `/hubs/player`)
`DownloadProgress{qid,0..90 download|90 finished|95 metadata|100 done,text}`, `QueueUpdated`, `PlaybackStateChanged`, `MaintenanceProgress{stage,label,percent,detail,status}`.
Hubs require auth (sockets via `?access_token=` JWT or `hth_` PAT); connections auto-join `user:{id}` from token claims — no client-supplied user id.

### Observability
- `POST /logs/client {message,stack,route}` (replaces `js_log` + `onerror/unhandledrejection` bridge).
- Serilog flow: console + rolling file always on; `Error`/`Fatal` also INSERT into the lab `logs` table via `PostgresLogSink` (bounded async channel, batched, self-swallowing) so myhomelab /logs shows Hathor failures.

---

## 5. Frontend (`frontend/hathor-web`)

Vite + React + TS + Tailwind (tokens: `bg-zinc-950 #09090B`, glass `bg-black/40 blur-2xl border-white/5`, `orange-500 #F97316 / orange-400 #FB923C / orange-600 #EA580C`) + React Router + TanStack Query (Dapper-backed queries) + Zustand (`playerStore`: current, queue, `is_custom_queue`, `unshuffled`, shuffle/repeat/volume/crossfade, `firstPlay`, `instanceId`) + Howler/`<audio>` + SignalR client + shadcn dialogs.

Routes: `/ (Home dashboard: All Songs card + Daily Mix card + strips)`, `/songs`, `/artists/:name`, `/albums/:title`, `/mix`, `/podcasts`, `/download`, `/playlists`, `/playlists/:id`, `/history (Download|Played tabs)`, `/settings`.

Components: `Shell (collapsible sidebar + playlists-submenu)`, `PlayerBar (shuffle/prev/play/next/repeat, seek, volume+mute+_previousVolume, 80ms throttle)`, `QueueSheet (responsive overlay <xl / panel ≥xl)`, `LyricsSheet (mask fade, romaji toggle)`, `NowPlaying (fullscreen, np-volume)`, `SongRow (⋮ + right-click SongMenu, lazy cover via IntersectionObserver + _coverCache, 4-bar visualizer, active ring)`, `PodcastRow (bento menu, tag pills, Get Metadata lazy)`, `EditSongModal (Title/Artist/Album/Year + Cover upload/REMOVE)`, `AddPlaylistModal`, `DownloadJobsList (global persistent toasts + History panel, retry/redownload)`, `StartupToast (ffmpeg/libs progress + Retry)`, `SongMenu (Play/Next/Add/Edit/Add-to-Playlist multi/Delete/Get Metadata/Tags…)`.

State rules ported: pull `GET /player/state` on load (replaces `load_current_song/get_current_song_ui_state` race), `queue_source` rebuild with stale fallback, strip queues (`recently_played/downloaded`, `daily_mix {id:date}`, `podcast {id:file}`, `all_songs`), keyboard `Space/←→±10s/↑↓`, Media Session API (title/artist/artwork + handlers) replacing SMTC.

### 5.1 Visual identity — clone desktop, reuse original code (token-saving)

Goal: web looks identical to `Music Player/ui` unless a control is desktop-only (titlebar/resize zones). Do not redesign. Copy, don't reinvent.

**Copy verbatim into `frontend/hathor-web/src/styles/` and `public/`:**

| Original | Reuse as-is | Notes |
|---|---|---|
| `ui/css.css` (202 lines: scrollbar, `visualizer-bounce` + 4 `.visualizer-bar` timings, range-slider orange gradient + thumb, `.active-nav`) | `src/styles/hathor.css` verbatim | Keep `--controller-height:112px`, `--range-percent` JS contract (`updateSliderProgress`), `body.lyrics-active #search-header` rule |
| `ui/input.css` (`@import "tailwindcss"`) | keep identical; build with same `@tailwindcss/cli -i input.css -o output.css` flow (`main.py:17`, `package.json`) | Same Tailwind version as desktop `package.json` — do not upgrade major |
| `ui/cdn.min.js`, `notyf.min.js/css`, `sweetalert2/` | replace with npm equivalents BUT keep class contracts: Notyf `position right/bottom`, Swal `swal-dark` glass theme (`index.html:14` glass CSS) → port the `<style>` block to a `SwalDark.ts` theme file | Toasts/dialogs must look identical even though libs are npm, not vendored |
| `ui/index.html` shell (ambient glows `bg-orange-500/10 blur-[120px]`, sidebar `w-64 border-orange-500/10 bg-black/40 backdrop-blur-2xl`, bottom bar `h-28 border-t border-orange-500/10 bg-black/40`, queue sidebar breakpoints, lyrics mask fade) | port JSX 1:1: `Shell.tsx` ← sidebar markup, `PlayerBar.tsx` ← `#controls` markup, `QueueSheet.tsx` ← `#queue-sidebar`, `LyricsOverlay.tsx` ← `#lyrics-container-view` | Keep all Tailwind classes; only swap `onclick="window.*"` for React handlers calling the same REST endpoints; drop `#titlebar` + `#window-resize-zones` (desktop-only) |
| `ui/views/*.html` + `ui/modals/*.html` | convert each to a route/component keeping layout classes: `home.html` cards/strips → `Home.tsx`, `all_songs/artist/album/daily_mix.html` table headers (`grid-cols-[minmax(0,4fr)_minmax(0,2.5fr)_100px_150px_auto]`, sort arrows `▲/▼`) → `LibraryTable.tsx`, `podcasts.html` bento menu + tag chips/modals → `Podcasts.tsx`, `download_songs.html` search card + `dlModeSong/dlModePod` toggle + trending tags + `global-downloads-container` toasts → `Download.tsx`, `playlist_home/view.html` grid + overlay play button → `Playlists.tsx`, `history.html` tabs + job list + pagination → `History.tsx`, `settings.html` rows + `xf-switch` toggle CSS → `Settings.tsx`, `edit_song.html` / `add_playlist.html` → `EditSongModal.tsx` / `AddPlaylistModal.tsx` | Mechanical conversion: HTML → JSX (`class`→`className`), inline `<script>` → hooks (`useQuery` replaces `pywebview.api.*`, SignalR replaces `evaluate_js` pushes, `IntersectionObserver` replaces `lazyRowObserver`). Keep every `id` used by CSS/JS (`#progressSlider`, `#volumeSlider`, `#queue-body`, `#lyrics-container`, etc.) so copied CSS keeps working |
| Icons (inline Heroicon SVGs: brand speaker, home, download, mic, list, clock, gear) | copy SVG paths verbatim into `src/components/icons.tsx` | Zero visual drift, zero new design tokens |

**Identity checklist (must-match):** `bg-zinc-950`, `border-orange-500/10`, `text-orange-400` states, `rounded-2xl` cards, `HeaderIconTile (w-12 h-12 rounded-xl bg-orange-500/10 border-orange-500/20)`, active-nav `bg-orange-500/10 + 4dp indicator + orange icon`, playlist hover play overlay, history `ring-orange-500/40` active row, strip cards `w-36 sm:w-44`, startup toast styling.

**Token-saving rule:** never rewrite CSS/HTML from scratch while the desktop file exists — `cp` then adapt bindings. New code only for: API calls (fetch vs `pywebview.api`), SignalR (vs `evaluate_js`), `<audio>` (vs `pygame`), Media Session (vs SMTC).

### 5.2 Reusable component catalog (`src/components/ui/`)

Every view composes from this catalog — no inline SVGs, no copy-pasted Tailwind patterns in views. One definition per visual pattern; the desktop clone stays pixel-identical because the classes live in exactly one place.

| Component | File | Replaces duplication in | Future consumers |
|---|---|---|---|
| `Icon` (`name` union: brand, home, musicNote, play, pause, prev, next, shuffle, repeat, search, x, plus, download, clock, gear, mic, list, tag, check, warning, dots) | `ui/icons.tsx` | Inline SVGs in Shell, PlayerBar, Home, Songs, Login | Every view/modal/sheet |
| `ControlButton` (ghost + `active` orange state), `PlayCircleButton` (orange `w-11`), `PlayPauseButton` (white `w-14`) | `ui/buttons.tsx` | PlayerBar 5-button cluster, Songs play-all, Home strip overlay | QueueSheet, NowPlaying, Podcasts rows, Playlist view |
| `CoverArt` (img or music-note fallback) | `ui/CoverArt.tsx` | PlayerBar `w-14`, Home strip, Songs row `w-10` | History rows, queue rows, NowPlaying art |
| `Brand` (gradient tile + wordmark, sized) | `ui/Brand.tsx` | Sidebar brand, Login brand | StartupToast, error pages |
| `ViewHeader` (`icon` + `title` + `subtitle` + `actions`, full/compact) | `ui/ViewHeader.tsx` | Home header, Songs header | All 11 views (artist, album, mix, podcasts, download, playlists, history, settings) |
| `SearchInput` (expanding pill + clear button) | `ui/SearchInput.tsx` | Songs search bar | Every list view (mix, artist, podcasts, history) |
| `SortableHeader` (grid header + `▲/▼` arrows, generic keys) | `ui/SortableHeader.tsx` | Songs Title/Album/Duration header | Daily mix, artist/album, history tables |
| `SongRow` (4-col grid, cover, active `ring-orange-500/40`, duration) | `ui/SongRow.tsx` | Songs table rows | Playlist view, history, mix, artist/album, queue (via `actions` slot for ⋮ menu) |
| `StripCard` (`w-36 sm:w-44`, hover play overlay) | `ui/StripCard.tsx` | Home Recently Added | Recently Played strip, podcast strips |
| `Modal` (glass shell, backdrop-close, title) | `ui/Modal.tsx` | — (new) | EditSong, AddPlaylist, tag manager/assign, preview video, delete confirms |
| `TextField`, `PrimaryButton`, `GhostButton` | `ui/fields.tsx` | Login form | All modals, download batch import, settings rows |
| `LoadingState` (spinner), `EmptyState` (title + hint) | `ui/states.tsx` | Home/Songs loading + empty blocks | Every async view |
| `ToggleSwitch` (`xf-switch` clone) | `ui/ToggleSwitch.tsx` | — (new, from `settings.html` CSS) | Settings crossfade + future toggles |
| `useDebouncedValue` | `ui/hooks.ts` | Songs ad-hoc `onSearch` timer hack | Every search input |

**Reuse rules (enforced in review):**
1. Views contain layout + data hooks only — any Tailwind block appearing twice moves to `ui/`.
2. No raw `<svg>` outside `icons.tsx` (Playwright snapshot pins the catalog, not each view).
3. `SongRow`/`StripCard`/`CoverArt` take `song` + callbacks; view-specific extras go through `actions` slots, never forks.
4. New views (Phase 2+) must reuse: header → `ViewHeader`, search → `SearchInput` + `useDebouncedValue`, tables → `SortableHeader` + `SongRow`, dialogs → `Modal` + `fields`, states → `states`.

---

## 6. Cross-cutting Decisions

- **Playback:** server streams (206 Partial Content), client decodes; crossfade client-side WebAudio equal-power ramp (`cos/sin`, 50ms steps), setting synced via API; manual next/prev/seek instant (cancel ramp).
- **Ingest:** `YtDlpService` spawns `yt-dlp` (`-f bestaudio/best -x --audio-format mp3 --audio-quality 320K`), `FFmpegService` resolves PATH → bundled; Hangfire enforces limit; iTunes enrichment post-hook before safe-rename; podcasts skip iTunes, keep uploader title/author.
- **Auth:** ASP.NET Identity + JWT; per-user storage isolation (`/storage/{userId}/...`); background = per-user upload.
- **DB:** Local PostgreSQL via Npgsql EF Core (dev + Docker Compose `localdb`); MySQL/TiDB via Pomelo EF Core kept for remote-compat (desktop sync); Dapper uses the same connection string via factory (quoted identifiers + `UPPER(uuid::text)` UserId predicate on Postgres).
- **Jobs:** Hangfire (downloads with cancellation, daily-mix regen, sync-compat, maintenance). Replaces `DownloadManager` threads, `SyncWorker/PullWorker`, `DownloadService` foreground.
- **Search:** typo-tolerant fuzzy search everywhere (`Hathor.Domain.Services.FuzzySearch` backend + `utils/fuzzy.ts` frontend mirror): exact substring always matches, otherwise every query token must match a title/artist/album token within a small Damerau-Levenshtein distance; `pg_trgm`/`fuzzystrmatch` extensions enabled for DB-level trigrams.
- **Security:** `.env` never committed (`DB_HOST/PORT/USER/PASSWORD/NAME/SSL_CA`, TiDB `4000` vs MySQL `3306`, `certifi`/system-CA fallback → server Kestrel cert).

---

## 7. Gap Audit → Disposition (what v1 missed, now fixed)

| # | Gap (source) | Fix in this plan |
|---|---|---|
| G1 | Genre `TCON` dropped on edit (`services/metadata.py:182`, `Download.py:355`) | Keep `genre` in Domain + `PATCH /songs/{file}` |
| G2 | `standardize_volume/current_tab/browser` ignored (`database.sql`) | `lastRoute`, server cookies option, reserved field |
| G3 | Playlist/tag counts hardcoded `0 songs` (`ui/views/playlist_home.html:175`, `services/database.py:112`) | Dapper `with-counts` endpoints, live-files-only tag counts |
| G4 | Cover `REMOVE`/URL/mime handling (`services/metadata.py:223`) | `DELETE/POST /songs/{file}/cover`, `POST /playlists/{id}/cover` |
| G5 | `first_play/opening`, `_instanceId/_historyId` replay (`services/playback.py:1052`) | `instanceId/opening/firstPlay` in player API + store |
| G6 | Verbatim custom-queue restore wins; `clear/jump` slice rules (`services/playback.py:112`) | `Queue` aggregate ports exact rules |
| G7 | Podcasts excluded from history/mix; lightweight list (`services/playback.py:821`, `services/database.py:400`) | Domain rule + light vs full podcast endpoints |
| G8 | Edit-while-playing resume position (`services/metadata.py:193`) | `PATCH` returns `resumeSec` |
| G9 | Delete cascade + tombstones + queue evict (`services/metadata.py:75,133`) | `DELETE` endpoints with full cleanup + SignalR |
| G10 | Progress stages 0/90/95/100 + persistent toasts (`services/downloads.py:148`) | SignalR same stages + global container |
| G11 | `.txt` batch import + 1.5s throttle (`ui/views/download_songs.html:440`) | `POST /downloads/batch` |
| G12 | Safe filename + `Title (n).mp3` (`Download.py:121,370`) | `FileNamingService` |
| G13 | Cancel queued vs kill running + watchdog + dedupe (`QueueRepository.kt:144,220,93`) | `DELETE /downloads/{qid}`, `clear-completed`, `resetStuck`, watchdog |
| G14 | Trending RSS + preview modal (`ui/views/download_songs.html:406,305`) | `GET /metadata/trending`, client iframe modal |
| G15 | Lyrics cleaning + exact fallback + max-10 search (`services/lyrics.py:45,64,203`) | `LyricsService` ports rules |
| G16 | Romanization toggle preserving timestamps (`services/lyrics.py:17`) | `POST /lyrics/romanize` + client toggle |
| G17 | Per-instance history + Re-download gating (`ui/views/history.html:208,292`) | `instanceId`, `played-playlists`, `redownload` |
| G18 | Mix quotas/shuffle/prune/cached (`services/database.py:530,596`) | `DailyMixGenerator` + Hangfire + `mixDate` source |
| G19 | Rescan buttons, background live-apply, limit clamp, crossfade switch (`ui/views/settings.html`) | `POST /songs|podcasts/scan`, background endpoints, clamped settings |
| G20 | FFmpeg/yt-dlp maintenance + toast + Retry (`services/startup_maintenance.py`) | Docker ffmpeg + `/system/*` + SignalR |
| G21 | `hathor.log` + `js_log` bridge (`ui/index.html:495`) | `POST /logs/client` + Serilog |
| G22 | Global ⋮ + right-click menu + multi-playlist assign (`docs/USAGE.md:37`, `services/database.py:94`) | `SongMenu` + `PUT /songs/{file}/playlists` |
| G23 | Lazy rows/covers, `_coverCache`, visualizer, active ring (`ui/index.html:812`, `ui/css.css:39`) | Virtualized list + observer + `VisualizerBars` port |
| G24 | Responsive queue/lyrics/Now Playing, keyboard, mute restore, volume throttle (`ui/index.html`) | Responsive sheets + shortcuts + Media Session |
| G25 | `DownloadService`/Doze, `SyncWorker/PullWorker`, SAF, system volume/TLS (`FEATURE_PLAN.md:5`) | Hangfire + quota UI + per-user volume + Kestrel cert |

---

## 8. Unit / Integration Tests (xUnit + Vitest + Playwright)

Backend: **xUnit + FluentAssertions + NSubstitute (or Moq)**. Frontend: **Vitest + Testing Library**, E2E/visual: **Playwright**. Coverage gate: ≥80% on Domain + Mix/Queue/Lyrics/FileNaming; Dapper SQL covered by Testcontainers.

| Project | What to test (maps to gaps G1–G25) | Key cases |
|---|---|---|
| `Hathor.Domain.Tests` (pure, no I/O) | `DailyMixGenerator` (G18), `Queue` (G5/G6), `FileNamingService` (G12), `LyricsCleaning` (G15), `PlaybackState` (G7) | Mix quotas 2/5/13/rest, `min(50,n)`, shuffle, empty/small library; `restore_saved_queue` wins, `jump` slices `[i+1:]`, `clear` sets fallback false; filename strip/150-char/collision `Title (1).mp3`; clean ` - ` split + `(remaster|mix|live|feat)` strip + exact track-only match skips instrumentals; podcast play emits no `SongPlayed` history event |
| `Hathor.Application.Tests` | Commands/Queries handlers with in-memory EF + stubbed `IDapperQuery` | `UpdateSongMetadata` preserves genre + returns `resumeSec` when current (G1/G8); `DeleteSong` cascades + tombstones 3 rows + queue evict (G9); `MoveQueue/Reorder/Jump` rules (G6); `LogPlay` excludes podcasts (G7); `GetHistory` pagination `{items,total_pages,current_page}` files-must-exist (G17); `RenameTag` blank→400/duplicate→409 (podcast tags) |
| `Hathor.Infrastructure.Tests` | Dapper SQL on Testcontainers MySQL (G3/G17/G18) + TagLibSharp (G4) | `with-counts` song counts + tag `episode_count` live-files-only; history pages + `MAX(id)` incremental import; mix ranking `GROUP BY song_file ORDER BY cnt DESC`; cover APIC read/replace/`REMOVE` round-trip, mime sniff, http-URL fetch mocked |
| `Hathor.Api.Tests` | Controllers + SignalR hubs via `WebApplicationFactory` + JWT stub | Auth guard 401; API-key (`hth_`) auth + `player:read` vs `player:control` scope enforcement + revoke; headless `POST /player/play|toggle|next|prev|seek-by|mute` with empty-body + `Idempotency-Key` replay; `PATCH /songs/{file}` validation; `POST /downloads/batch` parses `.txt` lines + throttling mocked; `DELETE /downloads/{qid}` queued→cancelled vs downloading→cancel; `/sync/import` guarded old-DB tables + tombstone apply; hub emits `DownloadProgress` stages 0/90/95/100 (G10) |
| `Hathor.Worker.Tests` | Hangfire jobs (mocked yt-dlp/ffmpeg/iTunes/lrclib) | Concurrency clamp 1–20, dedupe in-flight URL, `is_podcast` routing + retry preserves flag, watchdog 5-min → failed, `resetStuck` on boot, mix regen daily + adopt-remote + prune, maintenance progress events |
| `frontend/hathor-web` (Vitest) | `LibraryTable` sort/search, `QueueSheet` reorder/jump, `LyricsSheet` LRC highlight + romaji toggle, `EditSongModal` REMOVE flow, `Download` toggle + batch, `History` per-instance highlight + redownload gating, `Settings` crossfade clamp | Sort keys Title/Album/Duration/DateDownload `▲/▼`; search filters title/artist/album; queue DnD calls `PUT reorder`; romaji preserves `[mm:ss.xx]`; REMOVE sends sentinel; Songs/Podcast toggle routes payload |
| Playwright | Visual parity vs desktop + journeys | Screenshot diff vs `Music Player/ui` (shell, Home, Library, Podcasts, Download, Playlists, History, Settings, modals, queue/lyrics sheets); journeys: search→download→tag→play→lyrics→playlist→history→mix; keyboard `Space/←→/↑↓`; Media Session smoke; responsive `<xl` overlay vs `≥xl` panel |

Run: `dotnet test` (backend), `npm run test / test:e2e` (frontend), `docker compose -f docker-compose.test.yml up` for Testcontainers fallback in CI. Each phase below must add its tests before marked done.

---

## 9. Phased Build (each = deployable)

1. **Skeleton + Dapper reads + visual shell:** sln + contexts + EF migrations + Identity + `DapperConnectionFactory` + `SongQueries/PlaylistQueries` + **copied `hathor.css` + `Shell/PlayerBar` JSX from `index.html`** + `GET /songs + /stream + /count` + PlayerBar playing seeded MP3. Tests: Domain `FileNamingService`, Dapper `SongQueries` (Testcontainers), API `GET /songs` auth + paging, Playwright shell screenshot vs desktop. Pass = list + stream + counts + pixel shell.
2. **Library + Playlists + Tags (clone views):** CRUD, covers (`REMOVE`/URL), artist/album exact views + images, counts, modals, bento/right-click menu, multi-assign — all by converting `all_songs/artist/album/playlist_home/view.html` + `edit_song/add_playlist.html` keeping classes/ids. Tests: metadata genre-preserve + `resumeSec`, delete cascade/tombstones, `with-counts`, rename-blank/duplicate guards, Vitest table sort/search + modal REMOVE flow.
3. **Queue/State + History + Mix:** player store (`firstPlay/instanceId/custom_queue/source`), rebuild + verbatim restore, history logging (podcasts excluded) + per-instance highlight + redownload, mix generator quotas + daily Hangfire + adopt-remote — clone `daily_mix/history.html` layouts. Tests: Domain mix quotas + queue slice rules, handler history pagination + podcast exclusion, Playwright restart-resume + mix-stable-per-day.
4. **Ingest + Enrichment + Lyrics (clone download view):** YouTube search + jobs + batch `.txt` + cancel/watchdog/dedupe + SignalR staged progress, iTunes single/multi + trending, safe-rename, lyrics get/search/save + romanize — clone `download_songs.html` card/toggle/toasts. Tests: Worker concurrency/dedupe/watchdog/`is_podcast` routing, API batch + hub stages 0/90/95/100, Vitest toggle + batch parsing.
5. **Podcasts + Settings + Maintenance + Logs (clone views):** lightweight list + details lazy + tag manager/assign modals + combined filter → queue, settings rows + rescans + background + limits + crossfade, `/system/*` + toast + Retry, `/logs/client` — clone `podcasts/settings.html` + `xf-switch` CSS verbatim. Tests: tag live-counts + combined filter, settings clamp, infra cover round-trip, Playwright podcast filter→queue journey.
6. **Sync-compat + External API + Polish + Hardening:** `export/import/push`, first-run import flow, **external control GA (`/api-keys`, `now-playing`, `toggle/seek-by/mute`, OpenAPI + curl docs, CORS/rate-limit)**, Media Session, keyboard, Now Playing/lyrics/queue responsive, visualizer, Docker Compose (API + Worker + MySQL/TiDB + ffmpeg). Tests: full gap matrix G1–G25, API sync guarded-tables + `MAX(id)`, headless control suite (PAT scopes + idempotency + SignalR convergence web↔remote), E2E visual diff suite vs desktop (all routes + modals + sheets) + coverage gate ≥80% Domain.

---

## 10. Open Decisions / Risks

- yt-dlp on server: process-spawn vs container sidecar; YouTube rate limits + cookie handling (`browser` setting).
- Storage: local volume vs S3-compatible; quota per user; byte-range + CDN caching for covers.
- Romanization library: Kakasi.NET vs WanaKana port — confirm Hepburn quality vs `pykakasi`.
- `standardize_volume` (ReplayGain?) — currently unused; either implement (FFmpeg loudnorm) or drop explicitly.
- TiDB vs plain MySQL in prod — keep Pomelo + Dapper ANSI-compatible SQL (no `RETURNING`, use `LAST_INSERT_ID()` equivalents).
