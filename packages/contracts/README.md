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

Do not change the wire shape in one app without updating the other two in the same PR.
