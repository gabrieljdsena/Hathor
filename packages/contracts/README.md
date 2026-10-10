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
- `Playlists.thumbnail`: TEXT (base64/url), BLOB normalized to UTF-8 text for remote
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

Do not change the wire shape in one app without updating the other two in the same PR.
