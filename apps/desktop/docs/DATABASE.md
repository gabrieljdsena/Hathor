# Database Schema & Remote Sync

Hathor stores everything locally in **SQLite** (`music_player.db`). On Windows the file lives next to the executable; on Linux it lives at `~/.local/share/hathor/music_player.db`. When configured, the library (songs, podcasts, playlists, lyrics, history, daily mix) can be pushed to / pulled from the **Hathor server** — catalog sync is always manual, via the Settings buttons. Playback history additionally flushes automatically through a small durable outbox after each play; there is no background catalog sync thread.

## Local schema (`database.sql`)

```sql
Songs(
  file            VARCHAR(255) PRIMARY KEY,   -- filename in the songs folder
  downloaded_link VARCHAR(255),               -- source URL (YouTube)
  title           VARCHAR(255) NOT NULL,
  date_download   DATETIME   DEFAULT CURRENT_TIMESTAMP,
  artist          VARCHAR(255)
)

Podcasts(                                     -- separate non-music library
  file            VARCHAR(255) PRIMARY KEY,   -- filename in the podcasts folder
  downloaded_link VARCHAR(255),
  title           VARCHAR(255) NOT NULL,
  date_download   DATETIME   DEFAULT CURRENT_TIMESTAMP,
  artist          VARCHAR(255)
)

Playlists(
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  title       VARCHAR(255) NOT NULL,
  description TEXT,
  thumbnail   BLOB                       -- cover image bytes
)

Song_Playlist(                              -- many-to-many songs <-> playlists
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  song_file   VARCHAR(255) NOT NULL -> Songs(file),
  playlist_id BIGINT       NOT NULL -> Playlists(id),
  date_added  DATETIME     DEFAULT CURRENT_TIMESTAMP
)

Settings(                                   -- single row (id = 1)
  id                 INT NOT NULL PRIMARY KEY,
  current_song       VARCHAR(255) -> Songs(file),
  current_playlist   INTEGER,
  current_volume     FLOAT,
  limit_downloads    INT,
  standard_volume    BOOLEAN,                -- (column: standardize_volume)
  current_tab        VARCHAR(255),
  window_width       INT,
  window_height      INT,
  background_path    VARCHAR(255),
  songs_path         VARCHAR(255),
  podcasts_path      VARCHAR(255),           -- separate podcasts/audio folder
  browser            VARCHAR(50),
  queue_songs        TEXT,                   -- persisted custom queue (JSON filenames)
  custom_queue       INTEGER DEFAULT 0,      -- 1 = restore queue_songs on launch
  queue_source       TEXT,                   -- playback context JSON {type, id}
  crossfade_enabled  INTEGER DEFAULT 0,
  crossfade_seconds  REAL DEFAULT 5
)

Download_Queue(                            -- download job log (local only)
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  qid         UUID NOT NULL UNIQUE,
  url         VARCHAR(1000),
  title       VARCHAR(255),
  artist      VARCHAR(255),
  status      VARCHAR(50) DEFAULT 'queued',
  progress    REAL DEFAULT 0,
  error       TEXT,
  filename    VARCHAR(255),
  is_podcast  INTEGER DEFAULT 0,            -- route to podcasts folder/table
  created_at  DATETIME DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME DEFAULT CURRENT_TIMESTAMP
)

Daily_Mix(                                  -- today's generated mix (local only)
  mix_date    VARCHAR(10) PRIMARY KEY,      -- YYYY-MM-DD
  song_files  TEXT NOT NULL,                -- JSON filename list, play order
  created_at  DATETIME DEFAULT CURRENT_TIMESTAMP
)

Lyrics(
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  song_file VARCHAR(255) NOT NULL -> Songs(file),
  lyrics    TEXT           -- JSON: { synced: [...], plain: "..." },
  offset_ms INTEGER NOT NULL DEFAULT 0  -- highlight-timing correction, ms ±20000
)

Podcast_Chapters(                       -- chapter marks inside an episode
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  podcast_file VARCHAR(255) NOT NULL -> Podcasts(file),
  name         VARCHAR(255) NOT NULL,
  start_secs   REAL NOT NULL,            -- media offset in seconds
  end_secs     REAL NULL                 -- NULL = open-ended, never auto-skipped
)

Music_History(                              -- every time a song is played
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  song_file   VARCHAR(255) NOT NULL -> Songs(file),
  date_played DATETIME     DEFAULT CURRENT_TIMESTAMP
)

History_Sync_Queue(                         -- durable automatic history outbox
  history_id  INTEGER PRIMARY KEY -> Music_History(id)
)

Playlist_History(                           -- every time a playlist is played
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  playlist_id BIGINT       NOT NULL,
  date_played DATETIME     DEFAULT CURRENT_TIMESTAMP
)

Sync_Deletions(                             -- tombstones for remote deletion sync
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  table_name VARCHAR(255) NOT NULL,
  row_key    VARCHAR(255) NOT NULL,
  deleted_at DATETIME     DEFAULT CURRENT_TIMESTAMP
)
```

`Settings` is populated with a single default row (`INSERT OR IGNORE ... VALUES (1, ...)`).

> The playlist `thumbnail` column: local it's a `BLOB`; sync normalizes it to UTF-8 **text** (a base64 image) for the server.

## Server sync (`services/api_sync.py`)

The Hathor server (Postgres) is the source of truth — there is no remote database anymore. Full protocol in [Sync API migration](SYNC_API_MIGRATION.md).

## How the sync works

Catalog sync is **manual-only**: no background catalog thread exists. Two directions, both from Settings buttons (or the first-run prompt):

### Automatic history outbox

Every non-podcast play inserts `Music_History` and a matching
`History_Sync_Queue` row. `services/history_sync.py` debounces those rows and
pushes only the `MusicHistory` section via `POST /api/v1/sync/import`. The
outbox is cleared only after the server accepts the import, so offline/failed
pushes retry on the next play or app start. This path never blocks playback.

### Push — "Push to Server" (`Api.sync_local_to_remote` → `ApiSyncClient.push_once`)

1. Collect local rows + `Sync_Deletions` tombstones and `POST` them to `/api/v1/sync/import` (server applies scoped replaces and idempotent history inserts).
2. Upload raw MP3 bytes for files the server names in `missingFiles` (`PUT /api/v1/sync/files/{file}`).
3. Clear sent tombstones only after the server accepts them (a failed push retries everything idempotently).

### Pull — "Sync from Server" (`Api.sync_remote_to_local_and_download` → `ApiSyncClient.pull_once`)

`GET /api/v1/sync/delta?cursor=` merges changed rows (upserts, guarded link reconcile, daily-mix adopt + prune, tombstones applied incl. file removal), then streams missing files byte-identical from `GET /api/v1/sync/files/{file}` — songs into the songs folder, podcasts into the podcasts folder. The cursor (`sync_cursor.txt` next to the DB) advances only on full success. Podcast tag unassignments arrive as `podcast_tag_links` tombstones with key `<tagId>:<file>`.

### Connections

- Stdlib `urllib` over HTTP to the server saved by the login screen, using the authenticated session; no DB driver.
- 401 → the key is wrong or lacks scope; unreachable → "is the server running?".
- Sync failures return message strings — they never crash the app.

## First-run server import

If sync is configured and the local DB is brand new, `main.py` prompts the user. If accepted, `ApiSyncClient.pull_once()`:

1. Pulls the server library (songs, podcasts, playlists, lyrics, daily mix, history) into SQLite.
2. Downloads missing files directly from the server (each into its own folder).
3. Nothing else starts afterwards — further catalog syncs are manual via the Settings buttons; history continues to flush through its outbox.