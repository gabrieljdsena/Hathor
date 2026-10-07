# Sync contracts (shared by desktop / web / mobile)

Single source of truth for cross-device sync. Implementations:

- Desktop: `apps/desktop/database.sql` (SQLite schema) + `apps/desktop/sync.py` (`REMOTE_SCHEMA`, push/pull, tombstones)
- Web: `apps/web/src/Hathor.Infrastructure` (EF migrations) + `apps/web/src/Hathor.Api/SyncController.cs` (`export`/`import`)
- Mobile: Room schema + `SyncWorker`/`PullWorker` (to be added under `apps/mobile/`)

Rules all apps follow:

- Tombstone deletions propagate via `Sync_Deletions`
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
  NULL = open-ended): full replace scoped to the pusher's own episode files
  (shared remote, like `song_playlist`); episode delete propagates via a
  `podcast_chapters` tombstone keyed by file; pull upserts by id and never
  wipes local rows on empty snapshots

Do not change the wire shape in one app without updating the other two in the same PR.
