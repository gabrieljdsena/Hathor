# Sync migration: remote MySQL/TiDB → C# Web API (desktop-first)

## Locked decisions

- Clients keep local stores (desktop SQLite `music_player.db`; Android Room stays).
- Server Postgres is the source of truth; the C# API in `apps/web` is the only remote.
- LAN + per-device `hth_` API keys (`library:read` + `library:write`).
- Push uploads MP3 bytes; pull downloads MP3 bytes (no more YouTube re-download).
- Incremental delta from the start (`?cursor=` + deletions-since); full `export`/`import`
  only for first-run bootstrap.
- Scan / push / pull keep the same logic — only the transport changes
  (tombstones, scoped link replaces, incremental history via remote `MAX(id)`,
  last-writer-wins daily mix, add-only scan).

## Server dependency — IMPLEMENTED (commit with this slice)

Desktop's new client speaks this contract (all under `/api/v1/sync`,
`Authorization: Bearer hth_...`). Delta semantics: `UpdatedAtUtc`
server-stamped via a single `SaveChanges` hook; cursor
`<ticks>:<musicId>:<playlistId>`; time sections `>=` (idempotent overlap),
history exact by id, tombstones by `DeletedAtUtc`. Known gap: podcast
chapters have no snapshot section yet (sent, ignored, omitted).

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/sync/delta?cursor=` | Changed rows since cursor + new cursor + tombstones since cursor (**done**) |
| `GET` | `/sync/export?sinceId=` | Full bootstrap snapshot (already exists) |
| `POST` | `/sync/import` | Push rows + tombstones → `ImportResult { summary, missingFiles }` (**done**) |
| `GET` | `/sync/files/{file}?library=` | Download MP3 bytes (range; **done**) |
| `PUT` | `/sync/files/{file}?library=` | Upload MP3 bytes for new local files, 100 MB cap (**done**) |
| `POST/GET` | `/sync/playback` | Resume spot (already exists, unchanged) |

Server also needs `UpdatedAt` (server-set) on synced tables + migration.
Until the two **new** endpoints land, the desktop API path returns
`"Server sync endpoints unavailable …"` and the legacy path stays frozen.

## Desktop changes (this slice)

1. **New `services/api_sync.py`** — stdlib-only (`urllib`) HTTP client:
   - `api_configured()` → `HATHOR_API_URL` + `HATHOR_API_KEY` both set.
   - `ApiSyncClient.pull_once(db)` — `GET delta?cursor=` → reuse the existing
     merge semantics from `sync_remote_to_local_and_download` (upserts,
     link reconcile, `Daily_Mix` adopt) → stream missing files via
     `GET sync/files/{file}` to the exact remote filename → save cursor.
   - `ApiSyncClient.push_once(db)` — collect local rows + `Sync_Deletions`
     tombstones → `POST import` → `PUT sync/files/{file}` for files the
     server lacks → clear sent tombstones only on success.
   - Scan (`sync_local_songs_to_db` / `sync_local_podcasts_to_db`) untouched.
2. **Config** — `.env`: `HATHOR_API_URL=http://<server>:5051`,
   `HATHOR_API_KEY=hth_...` (implemented: `sync_backend()` in
   `services/api_sync.py`). If legacy `DB_HOST` is set (and no API URL),
   sync refuses with the retired message. Escape hatch while verifying:
   `HATHOR_SYNC_LEGACY=1` keeps the old JDBC path. `sync.py` (PyMySQL) is
   deleted once the API path is verified; `PyMySQL` already dropped from
   `requirements.txt`.
3. **Cursor store** — `sync_cursor.txt` next to `music_player.db`
   (avoids DDL churn; history still uses incremental `MAX(id)` semantics
   server-side).
4. **UI** — no changes: Settings Push/Fetch buttons keep calling
   `sync_local_to_remote` / `sync_remote_to_local_and_download`; `api.py`
   routes to the API client when configured.

## Android (later slice, same contract)

- New `data/api/ApiClient.kt` (OkHttp) replacing `remote/*` JDBC;
  `local.properties` `DB_*` → `API_BASE_URL + API_TOKEN`; drop
  `mysql-connector-java`; keep `PullWorker`, `SyncState` UI, Room schema,
  byte-progress downloads. Delete ytsearch sync fallback.

## Test checklist (desktop)

- [x] No API configured → friendly "not configured" message (no crash).
      (smoke-tested `sync_backend()` routing)
- [x] Legacy `DB_*` only → "retired" message, no JDBC attempt
      (`HATHOR_SYNC_LEGACY=1` escape hatch keeps the old path).
- [x] Merge logic: rows merge, links guarded-reconcile, lyrics/history/mix,
      deletions tolerated, cursor advances; second (empty) pull is a no-op.
      (smoke-tested against a stubbed delta)
- [x] Push collects rows + tombstones, clears them only on server accept.
      (smoke-tested against a stubbed import)
- [ ] LIVE (needs server endpoints from the table above): pull against seeded
      server — missing MP3s byte-identical; push new local file — row + bytes
      land server-side via `missing_files` → PUT.
- [ ] History incremental (no duplicates), links scoped-replace only own sets.
- [ ] Server down mid-pull → error string, cursor saved only on full success.
