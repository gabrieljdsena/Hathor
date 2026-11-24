# Web → Desktop parity backlog

Snapshot: **2026-11-15**, web at `ecc91e4`, desktop at `95396cb`. This compares
the React web app (`apps/web/frontend/hathor-web`) with the Python/pywebview
desktop app (`apps/desktop`).

It is implementation-oriented: each gap has an ID, affected files, acceptance
criteria and a phase. Check items off as they land.

**Status legend**

| Status | Meaning |
|---|---|
| `Missing` | No user-facing path on desktop |
| `Partial` | Exists but limited, hidden or not wired |
| `Bug` | Feature exists on desktop but does not work correctly |
| `Web-only` | Deliberately not worth porting (account/server UX) |
| `N/A` | Browser/desktop-shell difference with no user-facing gap |

**Architecture note:** web playback, downloads, Discover and AI DJ are
**server-owned**, and the browser talks to the API with a JWT. Desktop playback
and downloads are **local** (`pygame` + SQLite + `yt-dlp`), so server-backed
features must be adapted rather than copied:

- Desktop consumes the Hathor Web API with a Web account login (recommended)
  or a runtime `hth_` API key. Login sessions keep the access JWT in memory
  and the rotating refresh token in the OS credential store; the password is
  never stored. API keys remain available under Advanced for automation and
  plain-HTTP LAN installs. Login needs no extra scopes; keys need
  `library:read` + `library:write` for Discover, and `player:read` +
  `player:control` for AI DJ / cross-device playback.
- Server requests should go through Python (`services/hathor_api.py`) via the
  JS bridge. Never call the API directly from the webview: it avoids CORS,
  keeps the key out of the DOM, and reuses one auth path.
- Discover downloads use the local `DownloadManager` (`yt-dlp` + FFmpeg), not
  server download jobs. AI DJ tracks come from the server library, so a
  missing local file is fetched through `GET /sync/files/{file}`.
- Desktop remains the source of truth for local playback. The server queue is
  mirrored while AI DJ is active so prefetch/batch rotation and other clients
  stay coherent.
- Media playback inside the webview (`<audio>` for previews and Vohithe clips)
  is independent of `pygame.mixer.music`; the two must hand off explicitly.

---

## 1. Already at parity (do not redo)

These are working and should be preserved:

- **Playback & queue** — play/pause, next/previous, seek, volume persistence,
  shuffle, repeat, play-next queue, drag reorder, queue clear, current
  playback context persistence/restore.
- **Audio polish** — equal-power crossfade (1–12 s), gapless handoff, per-track
  loudness normalization, ducking-free chapter auto-skip, SMTC/media keys.
- **Now Playing & lyrics** — full-screen Now Playing, full-screen lyrics with
  large auto-following lines, tap-to-seek, ±20 s offset, podcast chapter
  lyrics with rebased highlighting, Romanization (currently always on).
- **Shell** — custom titlebar, collapsible sidebar, playlist submenu, background
  image, responsive player bar, keyboard shortcuts + help sheet.
- **Library** — All Songs, search/sort, edit metadata/cover/delete, playlists
  (create/edit/delete, multi-assign, cover upload), Daily Mix + regenerate,
  Artists/Albums list + detail with artwork, Podcasts with tags/chapters,
  History download/played tabs and pagination.
- **Downloads** — YouTube search, 320 kbps MP3, iTunes enrichment, `.txt`
  batch import, iTunes trending chips, embedded YouTube preview, job list and
  retry.
- **Sync** — delta pull/push with tombstones, file upload/download, first-run
  import, resume-spot push/pull and the resume dock.

---

## 2. Gap inventory

### 2.1 Entire screens / flows

| ID | Feature | Web source | Desktop status | Phase |
|---|---|---|---|---|
| D1 | **Runtime Web API configuration** — server URL + Web login, legacy API key fallback, Save/Test, scope hints, `.env` fallback | mobile P4.1; web auth/settings | Done — Web login + API key under Advanced | 0 |
| D2 | **Shared Web API client** — one base URL/auth/error layer for sync, Discover, AI DJ, player sync | `api/client.ts` | Done — `hathor_api.py` + `auth_session.py` | 0 |
| D3 | **Discover** — out-of-library recommendations, source filters, search, refresh, dismiss/feedback, downloads | `views/Discover.tsx` | Done | 1 |
| D4 | **Discover preview queue** — Play all, per-card play, prev/next, queue popover, seek, statuses, cross-view persistence | `hooks/useDiscoverPreviewQueue.ts`, `DiscoverPreviewPlayer.tsx` | Done | 1 |
| D5 | **Discover insights & tuning** — conversion/playback stats, learned source priors, Tune weights | `DiscoverInsightsCard.tsx` | Done | 1 |
| D6 | **AI DJ / Vohithe** — server-run sets, vibe brief, start/stop/new set, more/less, now playing/next set | `views/AiDj.tsx`, `store/aiDj.ts` | Done | 2 |
| D7 | **AI DJ voice & batch handoff** — intro/outro clips, ducking, prefetch, hold/retry at boundaries | `useStartDjSet.ts`, `store/player.ts`, `AiDjController` (mobile) | Done | 2 |
| D8 | **AI DJ entry points & visualizer** — Home card, player-bar vibe popover, ON AIR nav label, radial visualizer | `views/Home.tsx`, `components/DjVibePopover.tsx`, `VohitheVisualizer.tsx` | Done | 2 |
| D9 | **Home cards + nav** — AI DJ and Podcasts cards; Songs/Discover/AI DJ nav entries | `views/Home.tsx`, `components/Shell.tsx` | Done | 3 |
| D10 | **Move podcast ↔ songs** — both directions with confirmation, tags/chapters removal, tombstones | `views/Podcasts.tsx`, `components/ui/SongMenu.tsx` | Done | 3 |
| D11 | **Playlist cover removal + downscale** — remove cover; resize uploads to ≤800 px JPEG | `components/ui/PlaylistFormModal.tsx` | Done | 3 |
| D12 | **History row menus + full-tab queue** — play next/add queue/edit/playlists/delete/re-download; tab-scoped queue | `views/History.tsx`, `SongMenu.tsx` | Done | 3 |
| D13 | **Artist/Album shuffle play** | `views/Library.tsx` | Done | 3 |
| D14 | **Fuzzy/typo-tolerant search** — diacritics, tokens, Damerau-Levenshtein | `utils/fuzzy.ts` | Done | 3 |
| D15 | **Download already-owned guard** — confirm before creating `(1)` twins | `views/Download.tsx`, `api.checkDownload` | Done | 3 |
| D16 | **Pending metadata edits** — edit the playing file without rewriting it under the player | `pendingedit.test.tsx`, web client | Done | 3 |
| D17 | **Lyrics search/replace/delete** — explicit Search/Replace panel, chapter save target, delete cached lyrics | `components/LyricsSheet.tsx` | Done | 4 |
| D18 | **Romaji toggle** | `components/LyricsSheet.tsx` | Done | 4 |
| D19 | **Playback telemetry** — completion/skip segments for Discover quality | `audio/telemetry.ts`, `POST /player/playback-events` | Done | 4 |
| D20 | **Live cross-device player hub** — real-time play/pause/next/seek convergence and remote control | `playerHub.ts` | Done | 5 |

### 2.2 Smaller actions inside existing screens

| ID | Area | Web behaviour | Desktop current state | Files to touch | Phase |
|---|---|---|---|---|---|
| D21 | Discover card downloads | Button morphs into a % progress pill; feedback sent on completion | Done | `ui/views/discover.html`, `services/downloads.py` | 1 |
| D22 | Discover "Play all" | Previews the first 20 visible suggestions as a queue | Done | `ui/js/discover-preview.js`, `services/discover.py` | 1 |
| D23 | AI DJ spoken vibe | Record a short WAV, send to `/ai-dj/transcribe`, append text | Done | `ui/views/ai_dj.html`, `api.py` | 2 |
| D24 | AI DJ "hide upcoming" | Queue closed and only current track/set counts shown while active | Done | `ui/views/ai_dj.html`, `ui/index.html` | 2 |
| D25 | Nav "Songs" | Dedicated Songs route in the rail | Done | `ui/index.html` | 3 |
| D26 | Home Podcasts card | Episode count + play first episode | Done | `ui/views/home.html` | 3 |
| D27 | Download batch errors | Per-line queued/failed summary | Done | `ui/views/download_songs.html` | 3 |
| D28 | Metadata "Get Metadata" | Multiple iTunes candidates, pick one | Exists (`search_itunes_metadata_multi`) but verify the picker is reachable everywhere | `ui/modals/edit_song.html` | 3 |
| D29 | View crash recovery | Route error boundary keeps the shell/player alive | Done | `ui/index.html` | 5 |
| D30 | Large-library rendering | Server pages 200 songs / windows 200 names | Done — lazy song rows + 200-name chunks | `ui/views/*.html`, `ui/index.html` | 5 |

### 2.3 Web-only by design

Do not block desktop parity on these:

- Login/register, password change, active sessions, logout-all.
- API-key minting/revocation (`ApiKeys.tsx`); desktop only consumes a key.
- Multi-tab coordination, browser `localStorage` session semantics, unload
  keepalive.
- Server software installs/updates (FFmpeg/yt-dlp on the server, YouTube
  bot-check token provider, system maintenance). Desktop has its own local
  FFmpeg/yt-dlp maintenance and system-status row.
- Server paging/virtualization. Desktop queries local SQLite; use incremental
  rendering for RAM instead (D30).

---

## 3. Implementation plan

Work top-down. Phase 0 is required for every server-backed feature. Phase 3
items are independent and can be parallelized after Phase 0. Phases 1 and 2 are
independent of each other.

### Phase 0 — Foundation

- [x] **P0.1 — Runtime Web API configuration (D1)**
  - Add `hathor_api_url` / `hathor_api_key` to the local `Settings` row
    (`database.sql`, `api._ensure_schema`) and read them at call time in
    `services/api_config.py` (not import-time `settings.py`). Environment
    variables remain the fallback.
  - Add `services/api_config.py` (or extend `services/api_sync.py`):
    `get_api_config()`, `save_api_config(url, key)`, `api_configured()`,
    `clear_api_config()`, `test_connection()`.
  - Settings UI: URL, username/password sign-in, masked legacy key under
    Advanced (never render the full key after save), Save, Test, status line.
    Password login shows a warning on plain HTTP.
  - Make sync, resume spots and new clients read the runtime config without a
    restart.
  - Acceptance: pointing at a server works immediately; bad URL/key produces a
    clear message; `.env` still works; the full key is never logged.
  - Tests: persistence/fallback/masking; fake-server connection test.
  - **Done:** `services/api_config.py` reads the SQLite fields before `.env`,
    masks the key for the bridge, and exposes Test. Settings -> Web API has
    URL/key/Save/Test/Clear. `database.sql` and `api._ensure_schema` carry the
    new columns. Live connection test against the LAN server passed.

- [x] **P0.2 — Shared Web API client (D2)**
  - New `services/hathor_api.py` with a small `HathorApi` class: base URL,
    bearer token, `GET/POST/PUT/DELETE`, JSON decode, query helper, timeouts,
    `ApiError(status, message)`, media/clip URL helper with `?token=`, and
    `get_bytes()` for files.
  - Refactor `services/api_sync.py` to use it (keep the existing delta/import
    semantics and cursor files untouched).
  - Acceptance: one auth/error path; no duplicated urllib/auth code; sync
    behavior unchanged.
  - Tests: fake HTTP server covering 200/204/4xx/5xx, auth header, query
    encoding, non-JSON bodies.
  - **Done:** `services/hathor_api.py` handles JSON, token media URLs, byte
    streams and uploads. `api_sync.py` now delegates delta/import/file
    transport to it while keeping cursor/tombstone semantics.

- [x] **P0.3 — Shell and navigation scaffolding**
  - Add `discover` and `ai_dj` views and rail entries (`data-target`), plus a
    `Songs` entry; keep Home's All Songs card.
  - Add a global `#discover-preview-dock` above `#controls` in `ui/index.html`
    (outside `#main`) and a player-bar AI DJ slot.
  - Extend `_currentView` / `_sourceForCurrentView` for the new views; ensure
    view scripts are re-initialized on every navigation and stale globals are
    cleared.
  - Add a small view lifecycle contract (`window._disposeCurrentView()`) so
    Discover timers and AI DJ polling/audio are torn down when `navigateTo`
    replaces `#main`; the global preview dock and player bar stay alive.
  - Acceptance: empty Discover/AI DJ pages are reachable, nav highlights
    correctly, no console errors, player bar is unaffected.
  - Tests: manual smoke; JS console check.
  - **Done:** Songs/Discover/AI DJ rail entries, empty Discover/AI DJ views,
    a global preview dock placeholder and a hidden AI DJ player-bar slot.
    `navigateTo` now calls `window._disposeCurrentView()` before replacing
    `#main`. Inline scripts pass `node --check`.

- [x] **P0.4 — Desktop test/CI baseline**
  - Add `pytest` and `apps/desktop/tests/` with fixtures for temp SQLite,
    fake HTTP server and metadata files. `pygame`/`pywebview` imports should be
    isolated so pure tests run headless.
  - Add `.github/workflows/desktop-tests.yml`: `windows-latest`, Python 3.12,
    `pip install -r requirements.txt`, `pytest`, Node 20 + `npm ci` + Tailwind
    build. Cache pip/npm.
  - Acceptance: CI is green on an empty suite and runs on `apps/desktop/**`
    changes; no secrets or network calls in unit tests.
  - Tests: the workflow itself; one smoke test for `database.sql` schema init.
  - **Done:** `pytest.ini`, `requirements-dev.txt`, `apps/desktop/tests/` and
    `.github/workflows/desktop-tests.yml` (Python 3.12 + `pytest`, Node 20 +
    `npm ci` + Tailwind build). The suite is stdlib-compatible for local runs
    without pygame/pywebview; 79 Python tests + Discover/voice/fuzzy/batch/chunk JS smoke tests pass.

### Phase 1 — Discover

- [x] **P1.1 — Discover feed (D3)**
  - New `services/discover.py`: parse `{date, items[], cached}`; methods
    `get()`, `refresh()`, `feedback(payload)`, `insights(windowDays)`,
    `tuning()`, `tune()`. Normalize camelCase; tolerate missing optional fields.
  - Bridge methods on `Api`: `discover_get`, `discover_refresh`,
    `discover_feedback`, `discover_insights`, `discover_tune`.
  - New `ui/views/discover.html`: header with count/date/fresh, Refresh with
    spinner, search box, source chips (`all`, `similar`, `graph`, `tag`,
    `popular`, `artist`, `chart`, `llm`), responsive card grid with artwork,
    source badge, title/artist/album/year, reason, dismiss button.
  - States: loading, empty ("No suggestions right now"), error with retry,
    refresh notice; optimistic dismiss; feedback is best-effort.
  - Acceptance: feed loads, filters/search work, dismiss removes the card and
    posts feedback, refresh brings a fresh list, cards remain readable at
    narrow window sizes.
  - Tests: item parsing, filter/search, feedback body; optional Compose-style
    HTML snapshot is not available here — use JS DOM smoke or manual.
  - **Done:** `services/discover.py` + `Api.discover_*`; web-styled
    `ui/views/discover.html` with loading/empty/error states, search, source
    chips, dismiss, refresh, per-card video preview and the live insights card.
    Real LAN feed returned 50 items; inline JS passes `node --check`.

- [x] **P1.2 — Discover downloads (D21, D15)**
  - Add `Api.check_download(title, artist)` backed by the local `Songs` table:
    normalize lowercase, strip bracketed qualifiers, `feat.`/`featuring`
    tails, ` - <edition>` suffixes and punctuation; return `{owned, file}`.
  - Discover card download: call `check_download`; if owned, confirm
    "Download again / Cancel"; otherwise enqueue through the existing
    `DownloadManager` with `"<title> <artist> audio"` and the normal podcast
    flag `false`.
  - Match active jobs back to cards by normalized title/artist so the button
    morphs into a progress pill. Send `download` feedback only after submit
    succeeds; toast on done/fail; refresh Discover after completion.
  - Acceptance: owned songs prompt; progress appears per card; duplicate guard
    prevents double submits; downloaded songs appear in All Songs.
  - Tests: owned matcher; job key mapping; manual download.
  - **Done:** normalized local owned guard, SweetAlert confirmation, local
    `DownloadManager.submit(..., block=False)`, per-card progress from
    `get_download_jobs`, and download feedback. Direct Download-screen guard
    remains part of P3.7.

- [x] **P1.3 — Preview queue (D4, D22)**
  - Extract a small `ui/js/discover-preview.js` (plain module) owning:
    tracks/status (`pending|resolving|ready|playing|failed`), index, play/pause,
    prev/next, jump, close, position/duration, seek, queue popover, and a
    `notice` string.
  - Resolve through Python: `Api.resolve_discover_preview(title, artist)` calls
    `GET /youtube/search?q=<title artist>&limit=5`, then
    `GET /youtube/preview?id=<hit>&resolveAudio=true`, returning the first
    `audioUrl`. Cache by normalized `title|artist`; prefetch the next two
    tracks. If the server path fails, fall back to the local `yt-dlp` search
    plus a direct audio URL; if that also fails, mark `failed` and skip.
  - Play through one `<audio>` element in the webview (not `pygame`), so
    preview audio is independent of the main player. Keep the same element
    outside `#main` so previews continue across view changes.
  - Handoff rules:
    - Preview start calls `Api.pause_for_preview()`; Python pauses the main
      player (no auto-resume on close).
    - Starting/resuming any main-queue track calls
      `window.DiscoverPreview.stop()` through `evaluate_js` before audio starts.
    - Preview volume follows the main volume slider; preview stop does not
      auto-resume the main player.
  - UI: cover play button, "Play all" (first 20 visible), preview mini-dock with
    title/artist/position/seek, queue popover with per-track statuses, Stop.
    A per-card video button reuses the existing YouTube iframe/SweetAlert
    preview and advances to the next search candidate when one is unavailable.
  - Mutual exclusion: starting a preview pauses main playback (including AI
    DJ); starting any main track or an AI DJ session stops the preview. Do not
    allow a preview to run under an active AI DJ set.
  - Acceptance: a card preview starts reliably and tries multiple candidates;
    it survives tab changes; seek works; main/preview never overlap; failed
    candidates are skipped with a visible notice.
  - Tests: queue state transitions (Node test if extracted), resolver fallback,
    seek math; manual on Windows WebView2.
  - **Done:** `ui/js/discover-preview.js` owns the global queues/dock
    (play-all, prev/next, queue popover, seek, statuses, prefetch, per-card
    video). Python resolves `/youtube/search` + `/youtube/preview`, falls back
    to yt-dlp, pauses main on start, and stops previews from
    `PlaybackController.play_button` on every main start/resume. Preview volume
    follows the main slider.

- [x] **P1.4 — Discover insights and tuning (D5)**
  - Optional card after activity exists: conversion, playback completion,
    downloads/previews/clicks, unique artists, learned source priors.
  - "Tune weights" calls `POST /discover/tuning`; show applied/insufficient/
    unchanged/failed messages and refresh Discover after an applied tune.
  - Acceptance: card is hidden with no feedback; tuning never blocks the page;
    numbers match the API payload.
  - Tests: parsing and message mapping.
  - **Done:** insights card with conversion/playback/downloads/artists and
    source priors; Tune weights handles applied/insufficient/unchanged/failed
    and refreshes the feed after an applied tune. Real `/discover/insights`
    returned previews/artist data against the LAN server.

### Phase 2 — AI DJ / Vohithe

- [x] **P2.1 — AI DJ client and state (D6)**
  - New `services/ai_dj.py`: `state()`, `start(brief)`, `stop()`,
    `feedback(event, batchId, trackFile)`, `settings()`, `transcribe(wav)`,
    `clip_url(id)`; parse `enabled`, `configured`, `status`, `sessionId`,
    `settings` (`language`, `duckLevel`, `voiceLeadSeconds`), `currentBatch`,
    `nextBatch`, tracks and clip ids.
  - Bridge methods on `Api` and a lightweight event push to JS
    (`window.onAiDjState(payload)`); poll every 10 s while active and refresh
    immediately on track changes.
  - Respect `enabled`/`configured`: disabled/needs-token messages instead of
    failing silently.
  - Acceptance: state/start/stop/feedback work against the real server;
    disabled/configured states are clear; no secret/token logging.
  - Tests: JSON contract tests for batch/state shapes; fake-server round trips.
  - **Done:** `AiDjApi`/`AiDjController` parse camel/Pascal state and expose
    `ai_dj_*` bridge methods. A background poll refreshes state and pushes
    `window.onAiDjState`. Real LAN `/ai-dj/state` and `/player/state` reads
    verified.

- [x] **P2.2 — AI DJ playback and server queue sync (D6, D7)**
  - Add `services/player_sync.py`: `replace_queue(files, source)`,
    `play()`, `next()`, `state()` using `/player/queue`, `/player/play`,
    `/player/next`. Use source `{type: "ai_dj", id: sessionId}`.
  - On start, install the first batch as the local queue and play it; map every
    server file to the local library. If a file is missing, fetch it through
    `GET /sync/files/{file}` before playback (same as the mobile controller's
    first-batch rescue).
  - Mirror only forward local transitions to `/player/next`; prev/queue jumps
    must not advance the server. Use the polled state to append the prefetched
    batch to the local queue.
  - End the session when a non-AI-DJ queue starts (manual play, playlist, etc.)
    and restore normal queue behavior.
  - Batch boundary: detect the last track near its end, require
    `nextBatch` + outro/intro clips, play the voice sequence, then advance.
    Retry TTS; on persistent failure, show "Waiting for voice" and advance
    silently after the configured hold. Skipping the last track also triggers
    the next-batch transition; an explicit Stop/New set cancels the hold.
  - Acceptance: start plays batch 1 with intro; batch 2 follows with outro +
    intro; stop leaves the current queue; missing local files are fetched;
    starting another source ends the session.
  - Tests: boundary timing, forward-only queue sync, missing-file fallback,
    external-source cancellation.
  - **Done:** local batches are resolved from `Songs` (missing files fetched
    through sync), installed with `ai_dj` queue source, and mirrored with
    `/player/queue` + `/player/play`; forward track changes call `/player/next`
    with `ExpectedFile`, and each new batch replaces the server queue again so
    prefetch keeps following the session. Playback callbacks drive boundary/
    queue-exhaustion handling and external queues end the session.

- [x] **P2.3 — Voice bus and ducking (D7)**
  - Preferred: a JS `VoicePlayer` using an `<audio>` element per clip sequence
    (clip URL with `?token=`), sequential `onended`, cancel/replace by
    generation. WebView2 supports range-streamed audio; no extra dependency.
    A clip/playback failure is reported to the controller, which restores the
    duck level and advances rather than holding the queue forever.
  - Add a duck factor to `PlaybackController._effective_volume()` and the
    crossfade gain path: `out = user * normalize * duck`. Duck only while voice
    speaks, restore on completion/cancel, and settle ramps on pause/seek/skip.
  - Expose `Api.set_ai_dj_duck(level)` and controller voice stop/finish hooks;
    stop voice on track skip, stop, session end and view teardown.
  - Acceptance: voice is audible over music at the server's duck level; music
    returns to the exact previous level; crossfade and normalization never
    jump; no overlapping voice clips.
  - Tests: duck gain math; manual voice/boundary check.
  - **Done:** `ui/js/ai-dj-voice.js` sequences `?token=` clip URLs and reports
    completion/errors by generation. Voice follows the main volume slider
    (`get_current_volume` sync + `AiDjVoice.setVolume` pushes).
    `PlaybackController._effective_volume` and the crossfade ramp include the
    duck factor; Python sets/restores it around every clip set. JS voice smoke
    test covers sequencing and volume sync.

- [x] **P2.4 — Vohithe screen (D6, D8, D23, D24)**
  - New `ui/views/ai_dj.html` mirroring the web hierarchy:
    - Vohithe wordmark/hero, status dot (Ready/On air/Speaking/Waiting),
      language + set number.
    - Vibe composer textarea with Speak button; MediaRecorder WAV (16 kHz) is
      sent to `Api.ai_dj_transcribe`; append text, show recording/error states.
    - Start / New set / Stop, More like this / Less like this.
    - Now Playing card with cover/title/artist; Next Set card with track count
      and ready state; Voice card with a simple waveform/status.
    - Portrait + radial visualizer; reuse `apps/web/.../assets/vohithe-portrait.jpg`
      (copy into `ui/assets/`) and animate only while active.
  - Disabled/configured hints; error banner; waiting state at a boundary.
  - Hide upcoming tracks/queue while active (web contract); no track list.
  - Acceptance: feature is usable from the page alone; spoken vibe appends
    text; start/new set/stop reflect server state; visualizer stops when idle.
  - Tests: manual UI pass; transcript request body test.
  - **Done:** web-styled `ui/views/ai_dj.html` with portrait/radial visualizer,
    status/language/set, typed + spoken brief (`ai-dj-recorder.js`), start/
    new-set/stop, feedback, now playing/next set/voice cards, disabled/API
    hints. Queue is hidden via `body.ai-dj-active` while on air.

- [x] **P2.5 — AI DJ entry points (D8)**
  - Home AI DJ card: on-air badge, start/new-set button, disabled when the
    server has AI DJ off; navigate to the DJ page after start.
  - Player-bar DJ button opens a compact vibe popover (brief + start/stop),
    equivalent to the web `DjVibePopover`; while active, a compact "ON AIR"
    indicator is visible.
  - Sidebar label becomes `AI DJ - ON AIR` while active (same as web).
  - Acceptance: starting from Home or the popover enters the same session and
    the page reflects it; stop clears all entry points.
  - **Done:** Home AI DJ card, sparkles player-bar button + `DjVibePopover`
    parity, `AI DJ - ON AIR` nav label, and a shared `ui/js/ai-dj-state.js`
    state bridge (12 s refresh). All entry points call the same controller.

### Phase 3 — Home, library and download actions

- [x] **P3.1 — Home cards and navigation (D9, D25, D26)**
  - Add Podcasts card: episode count + play-first button (podcast queue
    source).
  - Add AI DJ card with on-air/start semantics (depends on Phase 2).
  - Add Songs, Discover and AI DJ rail entries; keep the existing Home cards
    and recent strips; use a 2×2 card grid on wide windows.
  - Acceptance: every card navigates/plays the right queue; empty states are
    clear; the rail still fits at the minimum window size.
  - **Done:** Home now has All Songs, Daily Mix (with play), AI DJ and Podcasts
    cards; Podcasts shows the episode count and plays the first episode with a
    `podcast` queue source. Daily Mix plays with `daily_mix`. Existing rail
    entries cover Songs/Discover/AI DJ.

- [x] **P3.2 — Move audio between libraries (D10)**
  - Add `metadata.move_podcast_to_song(file)` and
    `metadata.move_song_to_podcast(file)`:
    - Move the file between `settings.path` and `settings.podcasts_path` with
      collision-safe naming.
    - Update `Songs`/`Podcasts` and related rows. Use a delete-cascade from the
      source (same rows/tombstones as a delete) and ingest into the destination:
      moving an episode to Songs drops tag links, chapters and chapter/lyrics
      cache rows; moving a song to Podcasts drops its lyrics/history cache rows.
    - Record tombstones for the source row, lyrics and removed links; do not
      resurrect on the next push. The bare filename stays valid in both
      libraries, so queued entries can survive.
    - If the moved file is currently playing, stop it like a delete; remove it
      from the queue safely.
  - Add both menu actions to song and podcast context menus with confirmation
    copy ("tags/chapters are removed; the file is kept").
  - Acceptance: moves appear in the target library; source rows/tags/chapters
    are gone locally and after sync; reverse moves work; no crash if current.
  - Tests: file move + DB/tombstones, collision handling, current-track move.
  - **Done:** `services/library_moves.py` moves the file with collision-safe
    naming, migrates the destination row, deletes source-side links/caches
    and writes tombstones. Song and podcast menus expose the move with the
    required confirmation. Current playback stops, the queue drops the file,
    and `services/metadata.py` handles the wrapper/refresh.

- [x] **P3.3 — Playlist cover removal and downscale (D11)**
  - Add "Remove cover" to playlist create/edit/detail; store `NULL`.
  - Decode picked images with Pillow, downscale to max 800 px on the long edge
    and re-encode JPEG before storing the data URI. Apply to both
    `playlist_home.html` and `playlist_view.html` upload paths.
  - Acceptance: remove clears the thumbnail everywhere; large images no longer
    bloat SQLite/sync payloads; existing covers still render.
  - Tests: downscale helper (large/small/transparent input), remove path.
  - **Done:** `services/images.py` downscales/re-encodes playlist covers with
    Pillow; `Api.new_playlist`, `Api.update_playlist` and the native picker
    normalize data URIs. Create and detail editors expose Remove cover and
    store `NULL`/clear the preview.

- [x] **P3.4 — History menus and queue context (D12)**
  - Reuse the global bento menu for History rows: Play, Play Next, Add to
    Queue, Edit Info, Add to Playlists, Delete (music only), Re-download
    (download tab).
  - Prefetch the full active-tab file list and play rows through
    `populate_queue_from_list` with source `download_history` or
    `recently_played`; next/prev stay inside that tab.
  - Acceptance: row tap and menu Play queue the whole tab; menus match the
    library; re-download still works; deleted current tracks advance safely.
  - Tests: queue-source unit test; manual two-tab check.
  - **Done:** History rows open the global bento menu (button or right-click),
    keep Play/Re-download, and row plays prefetch a lightweight full-tab list
    (`get_*_history(..., include_cover=False)`) with `download_history` /
    `recently_played` sources. Deleting the current track clears it and
    advances the queue safely.

- [x] **P3.5 — Artist/Album shuffle (D13)**
  - Add a shuffle button next to Play all in artist and album detail views;
    shuffle the visible/full file list, start the first, use the existing
    `artist`/`album` queue sources.
  - Acceptance: shuffle changes start order but keeps all songs; play-all
    remains stable; source restoration still works.
  - **Done:** Artist and album detail views have a shuffle button that
    Fisher-Yates shuffles the full song list and starts it with the existing
    `artist`/`album` source.

- [x] **P3.6 — Fuzzy search (D14)**
  - Port the web `fuzzyMatch` algorithm (diacritics folding, token matching,
    Damerau-Levenshtein) into `ui/js/fuzzy.js` (or a Python helper used by the
    bridge). Apply to All Songs, playlists, podcasts, history, artists, albums
    and detail-page searches; keep exact/prefix hits first.
  - Acceptance: common typos match; no false positives on unrelated titles;
    typing remains debounced and fast on a 2k-song library.
  - Tests: table-driven cases mirroring `fuzzy.test.ts`.
  - **Done:** `ui/js/fuzzy.js` ports the web algorithm and is loaded globally.
    All Songs, Daily Mix, playlists, artist/album details, podcasts, artists/
    albums directories and Discover use it. Exact substrings short-circuit and
    the existing view order is preserved.

- [x] **P3.7 — Download owned guard (D15, D27)**
  - Use the same normalizer as P1.2 for direct Download searches; before
    enqueueing, check `Songs`/`Podcasts` (depending on mode) and ask
    "Already in your library — download again?".
  - Improve batch `.txt` feedback: per-line queued/duplicate/failed counts
    instead of a generic toast.
  - Acceptance: no silent duplicate files; cancel leaves no job; batch file
    summary is accurate.
  - Tests: matcher and batch parser.
  - **Done:** `check_owned_query`/`download_check_owned` use the Discover
    normalizer and pick `Songs` or `Podcasts`; direct and search-result
    downloads ask "Already in your library — download again?". Batch imports
    skip owned entries and report queued/duplicate/failed counts.

- [x] **P3.8 — Pending metadata edits (D16)**
  - If the edited file is currently loaded by `pygame` (or is the current
    song), hold the update in a pending map and apply it on the next track
    change/stop, then refresh the library and player metadata.
  - Show a "Pending edit — applies when the track changes" notice in the
    editor; editing a non-playing file remains immediate.
  - Acceptance: no file-lock/rewrite errors while playing; tags/cover apply
    exactly once; deleting a pending file cancels the pending edit.
  - Tests: pending map lifecycle; manual edit-while-playing.
  - **Done:** `MetadataManager` holds edits for the current file, flushes them
    on the next track/queue end/window close, and cancels them on delete/move.
    The editor shows the amber pending notice; non-playing files stay
    immediate.

### Phase 4 — Lyrics and playback polish

- [x] **P4.1 — Lyrics search/replace/delete (D17)**
  - Add a `Search/Replace` panel to the lyrics view:
    - Prefill track/artist (chapter-aware split, same as web).
    - Call the Web API `/lyrics/search` when configured, with the same
      cleaning/filters as web; fall back to the existing lrclib search.
    - Show candidates with track/artist/album/duration and synced/plain
      availability; Save replaces the cache row; Delete removes the current
      song or chapter row.
  - Keep offline behavior: cached lyrics still render; search shows a clear
    error/empty state.
  - Acceptance: search/replace/delete work for songs and chapters; cache and
    sync reflect the change; no duplicate rows.
  - Tests: cleaning/filtering, chapter key, delete; fake-server contract.
  - **Done:** the lyrics overlay has a `Search / Replace` panel with chapter
    prefill, Web API `/lyrics/search` with lrclib fallback, normalized
    candidates (instrumental/empty/duplicates filtered), preview, save/replace,
    remove-saved, and chapter-aware delete. Candidate cards show
    track/artist/album/duration and synced/plain availability. Saves clear
    stale lyrics tombstones; deletes record one for sync.

- [x] **P4.2 — Romaji toggle and chapter save target (D18)**
  - Stop romanizing unconditionally in `_lyrics_payload`; add a view toggle
    ("Romaji") that romanizes on demand (local pykakasi fallback).
  - Add a chapter target picker in the search panel: active chapter by default,
    whole episode optional. After saving to a different chapter, show a
    "Saved to <chapter> — jump there" banner.
  - Acceptance: raw Japanese is the default; toggle is instant and does not
    mutate the cache; chapter saves land on the right key and highlight clock.
  - Tests: romanizer output handling, chapter target selection.
  - **Done:** `_lyrics_payload` no longer romanizes; the topbar `Romaji`
    toggle calls the Web API romanizer (local pykakasi fallback) without
    changing cached lyrics. The search panel's chapter picker defaults to the
    active chapter, offers the whole episode, and shows a jump banner after
    saving to a different chapter.

- [x] **P4.3 — Playback telemetry (D19, optional)**
  - Report one segment per music track: `{segmentId, file, positionSec,
    durationSec, completed, skipped, startedAtUtc}` to
    `POST /player/playback-events`; client-generated id; report on natural end,
    manual skip, stop/close and track change; never report podcasts.
  - Fail silently; desktop continues to work when the server is unavailable.
  - Acceptance: one event per play, no duplicates on retry; Discover's quality
    stats improve; no network failures surface as playback errors.
  - Tests: segment lifecycle with a fake server.
  - **Done:** `services/telemetry.py` observes track changes/positions and
    posts one client-id segment per music track; natural ends report
    completed, manual skips report skipped, podcasts are ignored, and close
    flushes synchronously. Network/config failures are swallowed.

### Phase 5 — Cross-device and quality

- [x] **P5.1 — Live player hub (D20)**
  - MVP polling: `GET /player/state` every 2–5 s while the window is open and
    on AI DJ events; compare `queueVersion`/current file/position. Apply remote
    pause/resume/next/seek/volume to local playback only when the referenced
    file exists locally; otherwise show "Not on this device" and keep local
    audio unchanged.
  - Publish local transport mutations (`/player/play`, `/player/pause`,
    `/player/next`, `/player/seek`, `/player/volume`) so web/other clients
    converge. Ignore responses/broadcasts caused by this client's own
    in-flight calls.
  - Optional: replace polling with a Python SignalR client if a maintained
    dependency is acceptable; otherwise keep polling as the supported path.
  - Acceptance: pausing on web pauses desktop; next on web advances desktop
    when files exist; desktop controls update the web; no double playback;
    offline/401 never crashes the player.
  - Tests: reconciliation state machine; fake-server polling.
  - **Done:** `services/player_hub.py` polls 3 s, publishes local queue/play/
    pause/seek/volume with own-echo suppression, and applies remote transport
    only when files exist locally. Missing current tracks show a notice.
    AI DJ remains authoritative and is skipped by the hub.

- [x] **P5.2 — Resume dock polish**
  - Keep the existing spot push/pull. Align the dock's hide rules, device
    label, position formatting and "explicit tap only" behavior with web
    `ResumeBento`; also show it inside Now Playing.
  - Acceptance: own spot never echoes; resume restores queue/position; dismiss
    stays dismissed for that spot; no auto-play.
  - **Done:** player-bar dots + device/time-ago menu and a Now Playing banner;
    spots push `queueFiles`/`queueSource` and resume rebuilds the same local
    queue at the saved position. Server still excludes this device's own spot;
    nothing auto-plays.

- [x] **P5.3 — View crash recovery (D29)**
  - Wrap dynamic view loads/scripts in a try/catch that shows an inline error
    card with Retry/Home instead of leaving `#main` blank; keep the player bar
    and sidebar alive.
  - Acceptance: a thrown view script cannot break playback or navigation.
  - **Done:** `navigateTo` catches fetch/eval failures, always restores the
    main opacity, and renders a Retry/Home card in `#main`; the sidebar and
    player bar stay alive.

- [x] **P5.4 — Large-library performance (D30)**
  - Render All Songs/playlists/artists/albums incrementally (chunks of ~200
    rows on scroll) or add a virtual window; avoid mounting thousands of rows
    and base64 covers at once. Keep total counts/search semantics unchanged.
  - Acceptance: 5k-song library opens quickly, scrolling is smooth, memory
    stays flat; search/sort still cover the whole library.
  - Tests: manual 5k fixture; JS row-count smoke.
  - **Done:** song rows/playlists keep the virtual placeholder + lazy row
    observer (covers mount only near the viewport); artist and album
    directories use `ui/js/chunked-list.js` (200-name chunks on scroll) while
    counts/search still cover the full list. JS row-count smoke covers the
    chunker.

- [x] **P5.5 — Documentation and packaging**
  - Update `README.md`, `docs/CONFIGURATION.md` (runtime API config + scopes),
    `docs/ARCHITECTURE.md` (new services/voice path) and `docs/USAGE.md`
    (Discover/AI DJ/preview controls). Verify the PyInstaller build includes
    `ui/assets/` and new views.
  - Acceptance: fresh checkout + `python main.py` works; frozen build has no
    missing assets; docs match the UI.
  - **Done:** README/USAGE/ARCHITECTURE/DEVELOPMENT are current, and
    `music_player.spec` collects the whole `ui/` tree (including assets and new
    views). A source launch and the frozen `dist/Hathor/Hathor.exe` were both
    smoke-tested successfully; the PyInstaller archive includes
    `services/history_sync`, `database.sql`, `ui/index.html` and the Discover
    JS assets.

---

## 4. Verification

Run after each phase:

```powershell
cd apps/desktop
python -m pytest -q
```

Manual smoke pass on Windows (WebView2):

1. Settings → Web API: Save/Test against the real server; restart and verify
   the values persist; `.env` fallback still works.
2. Discover: load, filter, dismiss, refresh, play a preview, change tabs,
   seek, start a main song (preview must stop), start a preview while a main
   song plays (main must pause), download and verify the owned guard.
3. AI DJ: start, hear the intro, let a batch boundary run, verify voice over
   ducked music, stop, start again, start a normal song (session ends), and
   check the Home/player-bar/nav states.
4. Library actions: move an episode to Songs and back, remove/downscale a
   playlist cover, use History menus, shuffle an artist, search with a typo,
   download an owned song, edit the playing file.
5. Lyrics: search/replace/delete, chapter target, Romaji toggle, offset.
6. Cross-device (if implemented): pause/next on web, verify desktop converges;
   pause on desktop, verify web reflects it; resume spot from another device.

Suggested test matrix:

| Area | Suggested tests |
|---|---|
| API/config | fake-server auth, JSON, error and persistence tests |
| Discover | item parsing, source filter, feedback payload, owned matcher |
| Preview queue | status transitions, candidate fallback, seek, handoff |
| AI DJ | contract parsing, boundary state machine, duck gain, forward queue sync |
| Library moves | file/DB move, tombstones, collision, current-track safety |
| Playlists | cover downscale/remove |
| History | tab queue source, menu actions |
| Search | table-driven fuzzy cases |
| Lyrics | cleaning/filter, chapter key/target, delete, romanize toggle |
| Telemetry | one segment per play, retry idempotency |
| Cross-device | state reconciliation, own-echo suppression |

---

## 5. Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **Local-first desktop.** Playback/downloads stay local; server features use the Web API. | Matches the existing architecture and keeps the app usable offline. |
| 2 | **Web login first, API key fallback.** Sign in with the same account as the web app; keep a scoped `hth_` key under Advanced for automation and plain-HTTP LAN installs. Account/API-key management stays web-only. | Reuses the existing server auth system without breaking old installs. |
| 3 | **Discover downloads are local.** Never enqueue a server download job from desktop. | The desktop already owns files and the local download queue. |
| 4 | **Preview and AI DJ voice use webview `<audio>`; music stays on `pygame`.** | Preview clips must not fight the single `pygame.mixer.music` stream; Python ducking keeps voice-over-music under our control. |
| 5 | **Main/preview handoff is explicit.** Preview starts → pause main; main starts → stop preview. | Prevents overlapping audio without a second device-style player state. |
| 6 | **AI DJ mirrors the server queue while active.** Local playback remains authoritative; only forward transitions advance the server. | Required for prefetch/batch rotation and cross-client coherence. |
| 7 | **Move to Songs/Podcasts is local + tombstones.** Do not depend on the server move endpoint. | Keeps the action working offline and consistent with desktop's sync model. |
| 8 | **Live player hub starts as polling.** SignalR is optional. | No extra dependency/complexity; polling covers pause/next/seek convergence. |
| 9 | **Discover insight tuning is optional after core Discover.** | Core recommendations/downloads deliver the value; tuning needs accumulated feedback. |
| 10 | **Playback telemetry is part of the recommendation feed.** Desktop sends completion/skip segments automatically; the server merges events with `Music_History` fallback. | Keeps Discover/AI DJ current without waiting for a manual history push. |

---

## 6. Definition of done (per item)

- Feature is reachable from a normal UI path (no dead code).
- Persists locally and participates in sync where the data is synced.
- Handles empty/error/offline/401 states without crashing.
- Has at least one automated test at the service or JS level.
- `README.md`/`docs/` updated if behaviour or setup changes.
- Manual smoke check completed on Windows WebView2.

---

## 7. Progress log

| Date | Item | Notes |
|---|---|---|
| 2026-11-15 | **Plan created** | Gap inventory and phase plan drafted against web `ecc91e4` / desktop `95396cb`. |
| 2026-11-15 | **P0.1 — runtime Web API config** | SQLite URL/key + masked Settings UI, Test/Save/Clear, `.env` fallback and migration. Live LAN connectivity verified. |
| 2026-11-15 | **P0.2 — shared API client** | `services/hathor_api.py`; sync delta/import/file transport refactored onto it. |
| 2026-11-15 | **P0.3 — shell scaffolding** | Songs/Discover/AI DJ nav, placeholder views, preview dock, AI DJ slot, view dispose hook. |
| 2026-11-15 | **P0.4 — test/CI baseline** | 39 stdlib-compatible Python tests + Discover/voice JS smoke tests, pytest config, Windows desktop-tests workflow with Tailwind build. |
| 2026-11-15 | **P1.1 — Discover feed** | Web API feed/client + web-styled card grid, filters, search, dismiss, refresh, video preview and insights. |
| 2026-11-15 | **P1.2 — Discover downloads** | Local owned guard, SweetAlert confirmation, queued downloads, per-card progress/feedback. |
| 2026-11-15 | **P1.3 — preview queue** | Global audio queue/dock with seek, prefetch, server+yt-dlp resolution, main/preview handoff and volume sync. |
| 2026-11-15 | **P1.4 — insights/tuning** | Signal card, source priors, Tune weights and feed refresh. |
| 2026-11-15 | **P2.1 — AI DJ state** | State/start/stop/feedback APIs, parser, live `/ai-dj/state` and `/player/state` verification. |
| 2026-11-15 | **P2.2 — AI DJ playback** | Local batch install, missing-file fetch, forward-only `/player/next`, boundary/exhaustion callbacks and external-queue cancellation. |
| 2026-11-15 | **P2.3 — voice/ducking** | Global voice clip bus, duck factor in normal + crossfade gain, finish/error generations. |
| 2026-11-15 | **P2.4 — Vohithe screen** | Web-styled page, portrait/visualizer, spoken/typed vibe, commands, feedback, hidden queue. |
| 2026-11-15 | **P2.5 — entry points** | Home card, player-bar sparkles/popover, ON AIR nav label and shared state bridge. |
| 2026-11-15 | **P3.1 — Home navigation** | Podcasts card with episode count/play, Daily Mix play button, full card grid. |
| 2026-11-15 | **P3.2 — library moves** | Collision-safe song↔podcast moves, source cascades/tombstones, current-track stop and queue cleanup. |
| 2026-11-15 | **P3.3 — playlist covers** | Pillow downscale/JPEG normalize, remove-cover in create/detail, NULL storage. |
| 2026-11-15 | **P3.4 — history actions** | Global bento menu, tab-scoped queue sources, full-tab prefetch and safe delete advance. |
| 2026-11-15 | **P3.5 — artist/album shuffle** | Shuffle buttons with Fisher-Yates ordering and existing queue sources. |
| 2026-11-15 | **P3.6 — fuzzy search** | `ui/js/fuzzy.js` ported and applied across library, detail, podcast and Discover searches. |
| 2026-11-15 | **P3.7 — download guard** | Direct/batch owned checks for Songs/Podcasts and queued/duplicate/failed summaries. |
| 2026-11-15 | **P3.8 — pending edits** | Pending map for playing files, flush on track/stop/close, editor notice and delete/move cancellation. |
| 2026-11-15 | **P4.1 — lyrics panel** | Search/Replace panel, Web API candidates + lrclib fallback, preview/save/remove and chapter-aware delete. |
| 2026-11-15 | **P4.2 — romaji/chapter target** | Raw lyrics by default, on-demand server/local romanization, active-chapter picker and jump banner. |
| 2026-11-15 | **P4.3 — playback telemetry** | Segment observer, completed/skipped classification, podcast exclusion, close flush and fake-server contract. |
| 2026-11-15 | **P5.1 — live player hub** | 3 s reconciliation, local publish, remote pause/resume/next/seek/volume, own-echo suppression and missing-file notice. |
| 2026-11-15 | **P5.2 — resume dock** | Player-bar dots/menu/time-ago, Now Playing banner, queue/source snapshots and full queue restore. |
| 2026-11-15 | **P5.3 — view recovery** | Dynamic view fetch/eval failures render Retry/Home inline without touching the player shell. |
| 2026-11-15 | **P5.4 — large libraries** | Lazy song rows stay virtualized; artists/albums render 200-name chunks with full-list search and counts. |
| 2026-11-16 | **Automatic history sync** | Playback enqueues local `Music_History` ids; a debounced worker pushes only those rows via `/sync/import`. Pull dedupes by natural key/re-keys id collisions; manual Push clears only the snapshot it sent. |
| 2026-11-16 | **Live pipeline validation** | Deployed server health/readiness OK. A synthetic song+history probe confirmed natural-key dedupe, and a temp-DB run of `push_pending_history` pushed and cleared its outbox; probe rows were removed with tombstones. |
| 2026-11-16 | **P5.5 — packaging smoke** | Source launch and frozen `dist/Hathor/Hathor.exe` both stayed up; PyInstaller archive includes `services/history_sync`, `database.sql` and the full `ui/` tree. |
| 2026-11-23 | **Web account login** | `services/auth_session.py` signs in through `/auth/login`, keeps access JWTs in memory, stores the rotating refresh token in the OS credential store, refreshes single-flight, and retries 401s. Settings has sign-in/out, a plain-HTTP warning, and the legacy API-key fallback. |
| 2026-11-23 | **Desktop release** | `desktop-v1.0.2` publishes the Windows updater zip and portable Linux bundle with verified checksums. |
| 2026-11-23 | **Auth rollout** | Windows build 3 `1.0.2-hathor` and Android `0.4.24-hathor` support both login and API-key modes. GitHub Release runs are tag-driven (`desktop-v*`, `android-v*`). |
