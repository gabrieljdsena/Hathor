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
   `services/api_sync.py`). The old `sync.py` (PyMySQL) leg is deleted;
   `PyMySQL` dropped from `requirements.txt`.
3. **Cursor store** — `sync_cursor.txt` next to `music_player.db`
   (avoids DDL churn; history still uses incremental `MAX(id)` semantics
   server-side).
4. **UI** — no changes: Settings Push/Fetch buttons keep calling
   `sync_local_to_remote` / `sync_remote_to_local_and_download`; `api.py`
   routes to the API client when configured.

## Android — IMPLEMENTED (same contract, stdlib HttpURLConnection)

- New `data/api/SyncApi.kt` replacing `remote/RemoteDb.kt` + `remote/RemoteReader.kt`
  (both deleted) + `mysql-connector-java` dep; `local.properties` `DB_*` →
  `API_BASE_URL + API_TOKEN` (`BuildConfig` fields swapped).
- Pull (`RemoteSync.pullNow`, now with `Context` for the cursor pref):
  incremental delta, tombstones applied (rows + bytes), byte-identical
  downloads via `GET files/{file}` — the ytsearch re-download is gone from
  the sync path (kept for user-initiated downloads; `search_fallback` stays).
- Push (`RemoteWriter`, same `pushSongs`/`pushPodcasts` entry points):
  `POST import` + `PUT` bytes for `missingFiles`, tombstones cleared only
  on accept. New DAO deletes (`SongPlaylist`/`Lyrics`/`MusicHistory` by
  file, `PlaylistHistory` by playlist, `Playlist` by id — no schema change).
- Kept: `PullWorker` unique-work + constraints, `SyncState` UI, Room schema,
  first-run prompt (now gated on API config), rescan, folders.
- NOT compiled here (no Android SDK on this machine) — build in Android
  Studio and run one pull + one push over LAN before calling it done.

## Test checklist (desktop)

- [x] No API configured → friendly "not configured" message (no crash).
      (smoke-tested `sync_backend()` routing)
- [x] Remote-DB leg fully removed (`sync.py` deleted, JDBC pull deleted,
      no `DB_*` reads left in code).
- [x] Merge logic: rows merge, links guarded-reconcile, lyrics/history/mix,
      deletions tolerated, cursor advances; second (empty) pull is a no-op.
      (smoke-tested against a stubbed delta)
- [x] Push collects rows + tombstones, clears them only on server accept.
      (smoke-tested against a stubbed import)
- [x] LIVE (2026-10-10, deployed service): delta → import (`missingFiles`)
      → PUT → GET byte-identical → incremental delta with cursor advance;
      temp key revoked, test song deleted. Migration confirmed on live DB.
- [ ] History incremental (no duplicates), links scoped-replace only own sets.
- [ ] Server down mid-pull → error string, cursor saved only on full success.
