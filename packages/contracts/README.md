# Sync contracts (shared by desktop / web / mobile)

Single source of truth for cross-device sync. Implementations:

- Desktop: `apps/desktop/database.sql` (SQLite schema) + `apps/desktop/services/api_sync.py` (HTTP delta/files sync; `sync.py` is the retired MySQL leg)
- Web: `apps/web/src/Hathor.Infrastructure` (EF migrations) + `apps/web/src/Hathor.Api/SyncController.cs` (`export`/`import`/`delta`/`files`)
- Mobile: Room schema + `data/api/SyncApi.kt` (delta/files client) + `data/remote/PullWorker.kt` / `data/remote/RemoteWriter.kt`

Transport: the shared MySQL/TiDB remote is retired. Devices sync over the
Web API (`HATHOR_API_URL` + per-device `hth_` key, LAN): `GET /api/v1/sync/delta?cursor=`
for incremental pulls, `POST /api/v1/sync/import` for pushes (the result's
`missingFiles` are uploaded via `PUT /api/v1/sync/files/{file}`),
`GET /api/v1/sync/files/{file}?library=songs|podcasts` for byte-identical
downloads. JSON is camelCase; datetimes are ISO-8601 UTC.

Rules all apps follow:

- Tombstone deletions propagate via `Sync_Deletions` (delta carries them filtered by `DeletedAtUtc`)
- Delta cursor is opaque (`<utcTicks>:<musicMaxId>:<playlistMaxId>`); time
  sections use `>=` overlap (clients upsert idempotently — a repeat is free),
  history stays exact via id comparison; `UpdatedAtUtc` is server-stamped on
  every write (single DbContext hook — no write path sets it manually)
- History sync is incremental past remote `MAX(id)`
- `Playlists.thumbnail`: TEXT (base64/url), BLOB normalized to UTF-8 text for remote;
  the web API re-encodes data URLs to a max-800px JPEG before storing (sync shape unchanged)
- Daily mix: newest-wins, prune `< today`
- Files referenced by `file` key only; binaries on disk/S3, never in DB JSON
- `lyrics` carries `offset_ms` (INT, ms, ±20000, default 0): highlight-timing
  correction, upserted with the lyrics row on push, applied on pull
- Per-chapter lyric cache rows use `song_file = "{file}::chapter:{id}"`
  (desktop, web, mobile alike): exact file lookups never match them;
  episode delete/move drops them and tombstones each key
- `podcast_chapters` (`id`, `podcast_file`, `name`, `start_secs`, `end_secs`
  NULL = open-ended): upsert by id with cross-user re-key (like lyrics);
  episode delete propagates via a `podcast_chapters` tombstone keyed by file
  (server deletes by episode, like clients); pull upserts by id and never
  wipes local rows on empty snapshots. Wire section: `podcastChapters`
  (`Id`, `PodcastFile`, `Name`, `StartSecs`, `EndSecs`).
- Resume spots (cross-device "resume where I paused"): server table
  `Playback_Spots`, one row per (user, `device_id`), last write wins.
  `POST /api/v1/sync/playback` `{deviceId, deviceLabel?, file, positionSec,
  isPodcast}` upserts the caller's row (push on pause, tab-close beacon,
  and seek-while-paused; never while playing — a live position would
  clobber a genuine paused spot elsewhere). `GET /api/v1/sync/playback
  ?excludeDevice={id}` returns the newest spot from a DIFFERENT device
  (spots older than 30 days hidden); readers pass their own id so a device
  never echoes itself. Device ids are client UUIDs: web keeps one per
  browser (`hathor:device-id` in localStorage, shared by all tabs);
  mobile/desktop send theirs when they implement push (until then they
  simply get no spots). Nothing auto-plays — the pill is an explicit tap.

AI DJ state (`AiDj_Sessions`, `AiDj_Clips`, `AiDj_Feedback`) and the
`ai_dj` queue source are additive and web-only; they are not part of the
desktop/mobile sync snapshot. Other clients can still play the flat song
queue but do not render or speak the DJ commentary. See `ai-dj.md`.

Do not change the wire shape in one app without updating the other two in the same PR.
