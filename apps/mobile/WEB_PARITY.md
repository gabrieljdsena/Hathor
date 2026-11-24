# Web → Mobile parity backlog

Snapshot: **2026-11-14**, web at `ecc91e4`, mobile at `develop` (last feature
commit `2026-10-25`). This document compares the user-facing React web app
(`apps/web/frontend/hathor-web`) with the native Android app (`apps/mobile`).

It is intentionally implementation-oriented: each gap has an ID, affected
files, acceptance criteria and a suggested phase. Check items off as they land.

**Status legend**

| Status | Meaning |
|---|---|
| `Missing` | No user-facing path on mobile |
| `Partial` | Exists but limited, hidden or not wired |
| `Bug` | Feature exists on mobile but does not actually work |
| `Web-only` | Deliberately not worth porting (server/admin/desktop UX) |

**Architecture note:** web playback, downloads, Discover and AI DJ are
**server-owned**; mobile playback and downloads are **on-device**. Discover and
AI DJ are consumed through the Hathor Web API with a runtime server URL and
either a Web account login (recommended) or a scoped `hth_` API key configured
in Settings. Mobile's login uses the same username/password as the browser:
access JWTs stay in memory, the rotating refresh token is encrypted with an
Android Keystore key, and the password is never stored. API keys remain as an
advanced fallback for automation and plain-HTTP LAN installs. Login needs no
extra scopes; a key needs `library:read` + `library:write` for sync/Discover,
and `player:read` + `player:control` for AI DJ / cross-device playback /
playback telemetry. Playback completion/skip segments are posted automatically
to `/player/playback-events`; they never block local playback.

---

## 1. Gap inventory

### 1.1 Entire screens / flows missing on mobile

| ID | Feature | Web source | Mobile status | Size |
|---|---|---|---|---|
| W1 | **Discover** — out-of-library recommendations, source filters, preview queue, dismiss/feedback learning, insights + weight tuning, download suggestions | `views/Discover.tsx`, `components/DiscoverInsightsCard.tsx`, `hooks/useDiscoverPreviewQueue.ts` | Missing | L |
| W2 | **AI DJ (Vohithe)** — server-run sets, vibe brief + voice recording, start/stop/new set, more/less feedback, voice ducking, live visualizer, Home card and player popover | `views/AiDj.tsx`, `store/aiDj.ts`, `hooks/useStartDjSet.ts`, `hooks/useDjVibeRecorder.ts` | Missing | L |
| W3 | **Artists & Albums browse** — list all artists/albums, artwork heroes, search, play all/shuffle, per-group paging | `views/Library.tsx` (`Artists`, `ArtistDetail`, `Albums`, `AlbumDetail`) | Partial — only hidden subviews from a song's menu, no artwork/search/sort/play-all | M |
| W4 | **Login / register / account** — JWT login, registration gate, remember me, change password, active sessions, logout-all | `views/Login.tsx`, `auth/AuthContext.tsx`, `views/Settings.tsx` | Implemented — login/logout, remember me; password/session management stays web-only | — |
| W5 | **API-key management** — mint/revoke scoped `hth_` tokens | `views/ApiKeys.tsx` | Web-only; mobile only consumes a key | — |
| W6 | **Cross-device playback & resume** — SignalR player hub, queue/state broadcasts, “resume where you left off” from another device | `playerHub.ts`, `components/ui/ResumeSpot.tsx`, `ResumeBento.tsx`, `store/player.ts` | Missing; mobile queue restore is local-only | L |
| W7 | **Server/system administration** — FFmpeg/yt-dlp install/update, YouTube bot-check protection, loudness backfill, system status | `views/Settings.tsx` | Web-only by design | — |

### 1.2 Missing actions inside screens that already exist on mobile

| ID | Area | Web behaviour | Mobile current state | Files to touch | Size |
|---|---|---|---|---|---|
| S1 | Song actions | Edit title/artist/album/year + cover pick/remove + iTunes lookup + delete + pending-edit safety | Only play/next/queue + artist/album links. The full editor exists in **dead** `LibraryScreen.kt` | `ui/AllSongsScreen.kt`, `ui/SongRow.kt`, `ui/LibraryScreen.kt` (salvage), `data/MetadataRepository.kt`, `data/ArtworkRepository.kt` | M |
| S2 | Add to playlist | Multi-select modal, create-and-assign inline, bulk save | No reachable UI; `PlaylistRepository.setSongPlaylists()` exists but is only called by dead code | `ui/LibraryScreen.kt` (salvage), `ui/*Screen.kt`, `data/PlaylistRepository.kt` | S |
| S3 | Playlist cover | Edit form supports cover removal; uploads downscale to 800px JPEG | Upload only; no remove; stores full-size image data URI | `ui/PlaylistsScreen.kt` | S |
| S4 | Move podcast → song | “Move to Songs” with confirmation; tags/chapters removed, file kept | Missing | `ui/PodcastsScreen.kt`, `data/PodcastRepository.kt`, sync tombstones | M |
| S5 | Home dashboard | Cards for All Songs, Daily Mix, **AI DJ**, **Podcasts**, plus resume-from-device | Only All Songs + Daily Mix | `ui/HomeScreen.kt` | S |
| S6 | Daily Mix | Regenerate button (server-backed) | Generated once per day, no refresh | `ui/DailyMixScreen.kt`, mix persistence | XS |
| S7 | History | Covers, play button, per-row menu (play next/add/queue), full-tab queue context | Plain rows; no menus; row tap falls back to the general library queue | `ui/HistoryScreen.kt`, `MainActivity.kt` | M |
| S8 | Download | `.txt` batch import, iTunes trending chips, YouTube video preview, already-owned confirmation, bot-check hint | URL/search, audio-only preview, job list | `ui/DownloadScreen.kt`, `engine/DownloadEngine.kt`, `data/QueueRepository.kt` | M–L |
| S9 | Lyrics | Full-screen lyrics-first view, synced auto-follow, tap-to-seek, offset, romaji, search/replace, chapter lyrics | Full-screen view with large centered lines, active-line follow + seek, ±20 s offset, romaji, edit/delete, chapter lyrics; search uses the Web API | `ui/LyricsScreen.kt`, `data/LyricsRepository.kt`, `data/db/*` | Done |
| S10 | Search | Typo-tolerant/fuzzy matching (server) | Plain `contains` over title/artist/album | `ui/AllSongsScreen.kt`, shared util | S |
| S11 | Volume | Works and is server-synced | Applied live; slider lives on Now Playing | `ui/NowPlayingSheet.kt`, `playback/PlayerManager.kt` | Done |
| S12 | Runtime server setup | Login/API keys supply URL + token at runtime | Settings → Web API: server URL, username/password sign-in, Test; legacy API key under Advanced | `ui/SettingsScreen.kt`, `data/api/SyncApi.kt`, `data/api/AuthSession.kt` | Done |
| S13 | Playlist sidebar submenu | Expandable playlist list in the sidebar | Flat Playlists destination (acceptable mobile adaptation) | — | XS |
| S14 | Volume normalization | Browser-only compressor + measured loudness | Missing | `playback/PlayerManager.kt` | S (optional) |
| S15 | Queue reorder UX | Drag-and-drop + arrow buttons | Arrow buttons only (acceptable) | `ui/NowPlayingSheet.kt` | XS |

### 1.3 Web-only by design

These should not block mobile parity:

- Keyboard shortcuts and right-click context menus.
- Multi-tab coordination and browser `localStorage` session semantics.
- Server software installs/updates, FFmpeg/yt-dlp management, YouTube bot-check
  protection, system maintenance/status.
- Loudness backfill (mobile has no measured-loudness pipeline).
- Server paging/virtualisation (mobile pages locally; performance work only).
- Registration gate, password/session management (unless product wants
  accounts on mobile).

---

## 2. Implementation plan

Work top-down. Phase 0–2 are local-only, Phase 3 is polish, Phase 4 needs new
API surface and runtime auth.

### Phase 0 — Correctness and cleanup

- [x] **P0.1 — Fix playback volume (S11)**
  - `PlayerManager` must expose `setVolume(0f..1f)` and apply it to the active
    `MediaPlayer`; re-apply after every `reset()`, gapless handoff and
    crossfade.
  - Crossfade must treat the user volume as the base gain: `out = user * fadeOut`
    and `in = user * fadeIn` (currently fades hard-code `0..1`).
  - `MainActivity` restores the persisted volume at startup; the Now Playing
    Playing call `player.setVolume(...)` in addition to persisting it.
  - Acceptance: moving either slider changes output immediately; kill/relaunch
    keeps the level; crossfade never jumps to full volume.
  - Tests: `PlayerManager` volume math unit test if extracted; manual on AVD.
  - **Done:** `PlayerManager.setVolume()` + persisted restore in `MainActivity`;
    crossfade gains now scale by user volume; the Now Playing slider applies live.
    `CrossfadeGainTest` covers the math.

- [x] **P0.2 — Salvage the dead library editor**
  - `ui/LibraryScreen.kt` is never composed (only imported by `MainActivity`).
    Extract its reusable pieces into shared dialogs before deleting the screen:
    `EditSongDialog` (metadata + cover + delete + playlist assignment) and the
    artist/album grouping logic.
  - Do **not** wire the entire legacy screen; mobile navigation is shell-based.
  - Acceptance: `LibraryScreen.kt` is either removed or reduced to shared
    composables; no dead `LibraryScreen(` call sites remain.
  - **Done:** dead screen, artist/album grids, detail views and helpers removed.
    `SortHeader` + `EditSongDialog` now live in `ui/SongComponents.kt`; the
    unused `MainActivity` import is gone. P2.1 will reuse the grouping logic
    already present in `AllSongsScreen` instead of resurrecting the dead views.

- [x] **P0.3 — Decide navigation and runtime-config shape**
  - Choose where Artists/Albums live (Home cards vs rail destinations vs tabs
    inside All Songs). Recommendation: Home dashboard cards + a compact
    `Library ▸ Artists / Albums` sub-navigation, to avoid a crowded rail.
  - Decide whether runtime server/token setup (S12) is a prerequisite for
    Discover/AI DJ (recommended: yes) or whether build-time config stays for
    now.
  - Record decisions at the bottom of this file.
  - **Done:** decisions recorded in §4; implementation follows them.

### Phase 1 — Song and playlist actions (highest value)

- [x] **P1.1 — Metadata editor (S1)**
  - Reuse the salvaged `EditSongDialog` from P0.2 in the long-press/overflow
    menu of All Songs, Daily Mix, Home strips, playlist detail and History.
  - Fields: title, artist, album, year, cover picker/remove, iTunes candidate
    lookup, save, delete with confirmation.
  - If the edited file is currently playing, either stop/reload the track or
    defer the write until the track changes (web uses “pending edit”). Prefer
    the pending-edit approach so playback is not interrupted; show a
    “pending edit” chip or snackbar.
  - Acceptance: edits persist in tags and Room, sync push carries the new
    metadata, covers refresh in all lists, delete removes file + rows +
    tombstones and clears the player if it was current.
  - Tests: Compose test for dialog fields/save with a temp file; Room test for
    `updateMeta`/`deleteSong`; existing `SongDaoTest` covers DAO contracts.
  - **Done:** “Edit info” now opens the salvaged dialog from All Songs, Daily
    Mix, Home and playlist detail (History still pending P2.3). Edits of the
    playing file are staged in `PendingMetadataEdits` and applied when the
    track changes; delete clears pending edits and tombstones. Playlist
    checkboxes inside the dialog cover the add/remove flow (S2); a standalone
    dialog is still optional P1.2.

- [x] **P1.2 — Add/remove song from playlists (S2)**
  - Port the existing `EditSongDialog` playlist section into a standalone
    `SongPlaylistsDialog`: checkbox list of playlists, current membership,
    save via `PlaylistRepository.setSongPlaylists()`, inline “New playlist”.
  - Expose from the same song menus; keep playlist detail’s remove button.
  - Acceptance: checking/unchecking updates membership immediately; new
    playlist can be created and assigned in one flow; playlist song counts
    refresh.
  - Tests: repository test for `setSongPlaylists` add/remove/no-op; Compose test
    for the dialog.
  - **Done:** `SongPlaylistsDialog` is reachable as “Add to playlists” from
    All Songs, Daily Mix, Home and playlist detail. Toggles save immediately,
    inline creation creates + assigns, and the playlist flow refreshes counts.
    `PlaylistRepositoryTest` covers add/remove/clear behaviour.

- [x] **P1.3 — Playlist cover removal and downscale (S3)**
  - Add “Remove cover” to the playlist edit/detail flow; `setThumbnail(id,
    null)` already supports it.
  - Downscale picked images to max 800 px JPEG before storing the data URI
    (mirror web `PlaylistFormModal`); this also keeps Room/sync payloads small.
  - Acceptance: remove clears the thumbnail everywhere; large photos no longer
    bloat the DB; sync round-trips the cover.
  - Tests: unit test for the downscale helper; Compose test for remove.
  - **Done:** detail header shows a “Remove cover” action; picked images are
    decoded with bounds/sampling and re-encoded as ≤800 px JPEG before storage.
    `PlaylistCoverTest` covers scaling and small-image passthrough.

- [ ] **P1.4 — Delete song from the song menu (S1, split if useful)**
  - Confirmation dialog; call `MetadataRepository.deleteSong()`.
  - If the deleted file is current/queued, remove it from the queue and advance
    or stop safely.
  - Acceptance: file/rows/tombstones removed; UI refreshes; no crash when the
    current track is deleted.
  - Tests: repository test for tombstones (`songs`, `lyrics`,
    `music_history`), queue test.

- [ ] **P1.5 — Daily Mix regenerate (S6)**
  - Add a refresh action on `DailyMixScreen`; regenerate the local mix for today
    and replace the list/queue context.
  - Acceptance: button produces a new mix with a new persisted snapshot;
    repeated taps are idempotent per day unless explicitly regenerated.
  - Tests: mix repository unit test.

### Phase 2 — Browse, dashboard and history

- [x] **P2.1 — Artists & Albums destinations (W3)**
  - Reuse the artist/album grouping already present in the legacy
    `LibraryScreen`/`AllSongsScreen`.
  - Artists list: name + artwork hero (iTunes lookup already exists in
    `ArtworkRepository`), search, song count. Artist detail: artwork, songs,
    play all, shuffle.
  - Albums list/detail: title + artist disambiguation, artwork, play all,
    shuffle.
  - Wire artist/album names in rows to these destinations (replacing the
    hidden subview-only path), while keeping the song-menu “Go to artist/album”
    shortcuts.
  - Acceptance: full library browsable by artist and album without going
    through a song; artwork appears when iTunes resolves; queue source is
    `artist`/`album` so playback context is correct.
  - Tests: grouping unit tests; Compose navigation test.
  - **Done:** `ui/LibraryBrowseScreen.kt` adds Artists/Albums list + detail
    views with search, lazy iTunes artwork, play-all/shuffle, song menus and
    edit/playlist actions. Hidden `Dest.Artists`/`Dest.Albums` are reached from
    Home cards; Compose test opens the Artists card. Row-level artist/album
    links inside All Songs remain the existing subviews for now.

- [x] **P2.2 — Home dashboard cards (S5)**
  - Add a Podcasts card (episode count, play first episode) and, once W2
    exists, an AI DJ card (start/new set, on-air state). Add a resume-from-
    device card when W6 lands.
  - Keep the two existing cards and recent strips.
  - Acceptance: cards match web semantics; tapping play starts the right queue;
    empty states are clear.
  - Tests: Compose smoke test for card presence/navigation.
  - **Done:** Home now has All Songs, Daily Mix, Artists, Albums and Podcasts
    cards. The Podcasts card shows the episode count and a play button for the
    first episode (podcast queue source). AI DJ/resume cards stay deferred to
    P4.3/P4.4.

- [x] **P2.3 — History rows and queue context (S7)**
  - Add cover, play button and long-press/overflow menu (play next, add to
    queue, metadata actions once P1.1 exists).
  - Prefetch the full active-tab file list so a row click queues the whole
    history tab, not the general library (mirror web `History.tsx`).
  - Acceptance: row tap and menu queue use `recently_played` /
    `download_history` context; re-download still works.
  - Tests: queue-source unit test; Compose row test.
  - **Done:** History rows show covers, active/playing state, long-press and
    overflow menus, edit/playlist actions, and queue the full active tab with
    `download_history`/`recently_played` sources. Re-download moved into the
    menu.

- [x] **P2.4 — Move podcast to Songs (S4)**
  - Add “Move to Songs” to the episode menu with a confirmation explaining that
    tags/chapters are removed and the file is kept.
  - Move the file between podcast and music folders, update Room, write
    tombstones for podcast/tags/chapters, and let the next push carry it.
  - Acceptance: episode disappears from Podcasts and appears in All Songs; tags
    and chapters are gone; sync push does not resurrect the podcast row.
  - Tests: repository test for local move + tombstones; manual sync round-trip.
  - **Done:** episode menu has “Move to Songs” with confirmation;
    `PodcastRepository.moveToSong()` moves the file, inserts the songs row,
    removes tag links/chapters/podcast row and records podcast/chapter
    tombstones. Filename collisions fail safely. Sync round-trip still needs a
    manual check.

### Phase 3 — Media polish

- [x] **P3.1 — Fuzzy search (S10)**
  - Port web `utils/fuzzy.ts` (or a small Kotlin equivalent) and use it in All
    Songs, Daily Mix, playlists, podcasts and artist/album lists.
  - Acceptance: common typos/partial tokens match; ranking keeps exact prefix
    hits first.
  - Tests: table-driven unit tests.
  - **Done:** `util/FuzzySearch.kt` ports the web/server algorithm (diacritics,
    tokens, Damerau-Levenshtein). All search boxes use it. Existing explicit
    sort controls still decide display order; there is no separate relevance
    ranking yet. `FuzzySearchTest` mirrors the web cases.

- [x] **P3.2 — Lyrics extras (S9)**
  - Add lyrics delete.
  - Add romaji toggle for synced/plain lyrics. The web calls
    `/lyrics/romanize`; on mobile either call the server (needs runtime config)
    or ship a local fallback and mark the feature server-dependent.
  - Add per-chapter lyrics: store lyrics under `file::chapter:{id}` (the sync
    contract already filters these pseudo-rows), target a chapter when saving,
    rebase the highlight clock to chapter start, and persist a per-chapter
    offset.
  - Acceptance: chapter lyrics search/save/highlight/offset work and survive
    sync; song lyrics are unaffected.
  - Tests: parser/offset tests; SyncApi test for chapter pseudo-rows already
    exists.
  - **Done:** lyrics delete (with confirmation) and a Romaji toggle using the
    local kana→Hepburn fallback (`LyricsRomanizer`; kanji passes through, same
    as the web fallback). Podcast lyrics gain an Episode/chapter selector,
    chapter-keyed cache rows (`file::chapter:{id}`), chapter-rebased highlight
    clock and per-chapter offsets. Chapter pseudo-rows are still filtered out
    by the mobile pull (`SyncApi`), so chapter lyrics stay local-only until the
    sync contract includes them.

- [x] **P3.3 — Download extras (S8)**
  - `.txt` batch import via `ActivityResultContracts.OpenDocument`; enqueue
    each URL.
  - “Already owned” check before enqueueing (normalize title/artist against
    Room, offer cancel).
  - iTunes trending chips (optional, easy once the endpoint is available).
  - Video preview: decide whether an embedded YouTube WebView is worth it on
    Android; audio preview may be a better mobile adaptation. If skipped, mark
    as a deliberate deviation.
  - Acceptance: batch files enqueue with per-line errors; duplicate guard works;
    preview continues to pause library playback.
  - Tests: batch parser unit test; ownership matcher unit test.
  - **Done:** “Import .txt” reads one URL per line (comments/blank lines
    ignored) and enqueues them with a queued/failed summary. Search results run
    a fuzzy “already owned” check and offer Download anyway/Cancel. Trending
    chips are deferred; video preview stays audio-only as the deliberate mobile
    adaptation. `DownloadBatchTest` covers the parser.

- [ ] **P3.4 — Optional polish**
  - Normalize volume using Android loudness/audio-session tools (S14) — only if
    the product wants it; web’s implementation is browser-specific.
  - Drag-and-drop queue reorder (S15) — current arrows are acceptable.
  - Playlist submenu (S13) — mobile’s flat list is acceptable.

### Phase 4 — Server-backed features

Prerequisite: **P4.1 runtime server/token configuration** (S12). Without it,
every feature below requires rebuilding the APK.

- [x] **P4.1 — Runtime server setup (S12)**
  - Settings screen fields for server URL + `hth_` token, “Test connection” and
    scope display; persist in `SettingsRepository`/DataStore with the existing
    `BuildConfig` values as defaults.
  - Surface missing scopes (`library:read/write`, `player:read/control`) before
    enabling Discover/AI DJ.
  - Acceptance: no rebuild needed to point at a server; bad URL/token produces
    a clear error; existing build-time config still works.
  - Tests: settings persistence; connection-test unit test with a fake HTTP
    server.
  - **Done:** `SyncConfig` now stores runtime URL/token in SharedPreferences
    (BuildConfig remains the fallback). Settings → Web API has URL, Web
    username/password Sign in, Sign out and Test, plus the legacy masked API
    key under Advanced. Login uses `AuthSession` (Keystore refresh token,
    single-flight refresh, 401 retry); API-key mode remains fully supported.
    `ApiContractTest` covers persistence and `AuthSessionTest` covers the fake
    login/refresh/401 paths.

- [x] **P4.2 — Discover (W1)**
  - Add a mobile API client for `/discover`, `/discover/refresh`,
    `/discover/feedback`, `/discover/insights`, `/discover/tune` (library
    scopes).
  - UI: recommendation cards with artwork, source badge/reason, search/source
    filters, dismiss, download. Mobile download should use the on-device
    `DownloadEngine`, not server jobs.
  - Preview: mobile-adapted queue/player (reuse `PreviewPlayer`); YouTube video
    preview is optional.
  - Feedback events: dismiss, preview, click, download (same semantics as web).
  - Acceptance: recommendations load, filters work, dismiss persists feedback,
    downloads land in the library, owned tracks are guarded.
  - Tests: API JSON contract tests (Robolectric/org.json), Compose filter/card
    tests.
  - **Done:** `DiscoverApi` + `DiscoverScreen` load `/discover`, refresh, filter
    by source/search, show iTunes art/reasons, dismiss with feedback, and
    download through `QueueRepository.submit` (on-device bytes). Previews use
    the same Web API `/youtube/search` + `/youtube/preview` path as the web
    (tries five hits, server-resolved audio URL) with an on-device fallback;
    the card shows Finding/Playing/Failed state, a large cover play button and
    a mini preview bar. Downloads resolve their YouTube candidate through the
    same server search first. Owned tracks prompt before re-download.
    Insights/tuning cards and multi-item preview queues are not ported yet.
    JSON contracts covered by `ApiContractTest`.

- [x] **P4.3 — AI DJ / Vohithe (W2)**
  - Needs a token with `player:read` + `player:control`.
  - Add API client for state/start/stop/feedback/transcribe/settings/clip.
  - UI: Vohithe screen with status, vibe text, start/stop/new set, feedback,
    now-playing/next batch, visualizer. Voice recording is optional for v1;
    text brief first.
  - Playback: receive batch tracks, play locally, duck music under voice clips
    (mirror web `useStartDjSet`).
  - Respect `enabled`/`configured` and show setup hints like the web.
  - Acceptance: start plays the first batch and intro voice; stop halts cleanly;
    feedback works; feature-disabled state is clear.
  - Tests: API contract tests; state-machine unit tests; manual on AVD.
  - **Done:** `AiDjApi` + app-scoped `AiDjController` keep the Web API as the
    source of truth: start/new set/stop, like/dislike feedback, server queue
    sync (`player/queue`, `play`, `next`), automatic batch rotation and
    prefetch. Vohithe's commentary clips are cached and played over ducked
    music (intro, outro + next intro at the boundary), with waiting/fallback
    states. Start also fetches a missing first-batch track on demand from the
    same sync file endpoint, so a partial library does not kill the session.
    The screen now mirrors the web hierarchy (hero, portrait +
    radial visualizer, status, vibe composer, now playing, next set, voice)
    instead of dumping batch JSON/text. Spoken vibe input records 16 kHz WAV
    and uses `/ai-dj/transcribe`. Contract covered by `ApiContractTest` and
    `DjBoundaryTest`; Compose states covered by `AiDjScreenTest`.

- [x] **P4.4 — Cross-device playback and resume (W6)**
  - MVP: push/pull resume spots (`/player/spot`, `/player/spot/latest`) and show
    a “Continue on this device” card (web `ResumeBento`) that restores the
    queue and seeks.
  - Later: full SignalR state mirroring / remote control of another client.
  - Acceptance: pausing on web and opening mobile offers the same track/position;
    resuming restores the queue context; no auto-play.
  - Tests: spot JSON contract tests; queue restore unit tests.
  - **Done (MVP):** `PlaybackApi` pushes a spot on pause
    (`/sync/playback`, device id/label, position, queue files, source) and reads
    the latest foreign spot on Home. The “Continue on …” card restores the
    local queue and seeks to the saved position; it only appears when the
    current file exists locally. Full remote control/SignalR mirroring is not
    implemented. Contract covered by `ApiContractTest`.

- [x] **P4.5 — Accounts (W4) — implemented**
  - **Decision changed:** mobile signs in with the same Web account as the
    browser. `/auth/login` returns a 15-minute access JWT and rotating refresh
    token; access stays in memory and the refresh token is encrypted with an
    Android Keystore AES/GCM key. The password is never stored.
  - Remember me maps to the server's 30-day session; otherwise 24 hours.
    Sign out revokes the current session server-side and clears Keystore.
  - Legacy scoped `hth_` keys remain under Advanced for automation/plain-HTTP
    LAN installs. Password/session management (change password, revoke other
    devices) stays in the web UI.

- [x] **P4.6 — Playback telemetry (D19)**
  - Report one segment per non-podcast music track to
    `POST /player/playback-events` with a client-generated id, position,
    duration and completed/skipped flags.
  - Close a segment on track change, repeat, queue exhaustion and service
    shutdown; keep pause/resume as one segment.
  - Best-effort delivery only: a missing/offline Web API must never affect
    local playback.
  - **Done:** `PlaybackTelemetry` observes `PlayerManager` ticks and lifecycle
    events; `PlaybackEventsApi` posts the same contract as web/desktop.
    Podcasts are excluded. `PlaybackTelemetryTest` covers classification and
    segment boundaries; `PlaybackEventsApiTest` covers the POST path, bearer
    auth and JSON body.

- [x] **P4.7 — Automatic Music_History sync**
  - Add a durable `History_Sync_Queue` outbox (Room v10); enqueue the local
    history id on every non-podcast play.
  - Debounce and push only `MusicHistory` via `POST /sync/import`; clear rows
    only after server acceptance, so offline/failed pushes retry on the next
    play or app start.
  - Dedupe pulled history by `(song_file, date_played)` and re-key a server id
    that collides with a different local row.
  - Keep the full Settings Push for catalog/files and clear only outbox rows
    included in that snapshot.
  - **Done:** `HistoryRepository` enqueues; `SyncRepository.pushPendingHistory`
    flushes; `RemoteWriter.historyPayload` emits the scoped payload;
    `RemoteSync` natural-key dedupe/re-key; `HistorySyncOutboxTest` covers the
    queue, natural key and bounded manual-push clearing. Saving Web API
    settings also triggers a pending flush.

- [x] **P4.8 — Auth background and edge cases**
  - Pass the failed access token into `AuthSession.accessToken(...)` so
    concurrent 401s share one refresh instead of rotating twice and tripping
    reuse detection.
  - Retry sync JSON, file uploads, and file downloads once after a 401 with
    the refreshed token. Retry the app-update metadata check and APK download
    the same way.
  - Rebuild AI DJ clip URLs with the refreshed token when a media request
    returns 401.
  - Keep the session on offline/5xx failures; clear it only when the server
    rejects the refresh token.
  - **Done:** `SyncApi`, `HttpUpdateApi`, `UpdateRepository` and
    `DjVoicePlayer` now use the session-aware retry path; the legacy static
    API-key path still behaves as before. `AuthSessionTest` covers sync delta
    and file-download retries; `UpdateHttpApiTest` covers update metadata and
    APK download retries.

---

## 3. Verification

Run after each phase:

```powershell
cd apps/mobile
.\gradlew.bat testDebugUnitTest
.\scripts\test.ps1 -Device -Avd hathor_api34
```

Add tests alongside the feature:

| Area | Suggested tests |
|---|---|
| Volume | gain math unit test; manual AVD check |
| Metadata/delete | Room/`MetadataRepository` tests; Compose dialog test |
| Playlists | `setSongPlaylists` add/remove; cover downscale; Compose test |
| Artists/Albums | grouping/sort unit tests; navigation Compose test |
| History | queue-source unit test; row menu Compose test |
| Fuzzy search | table-driven Kotlin tests |
| Lyrics | chapter key/offset parser tests; existing `SyncApiTest` |
| Discover/AI DJ | JSON contract tests (Robolectric), state-machine tests |
| Playback telemetry | segment classification/boundary unit tests; POST path/auth contract test |
| History sync | outbox/enqueue/natural-key DAO tests; scoped import + clear against a fake server |
| Runtime config | settings persistence + fake-server connection test |

Manual smoke pass on BlueStacks: install the debug APK and exercise the new
screens (BlueStacks cannot run instrumentation APKs on this machine — use the
AVD for `connectedDebugAndroidTest`).

---

## 4. Decisions

Decided 2026-11-14 (P0.3). Revisit only if the product direction changes.

| # | Decision | Rationale |
|---|---|---|
| 1 | **Artists/Albums:** Home dashboard cards + a compact `Library ▸ Artists / Albums` sub-navigation launched from All Songs/Home; no new rail entries. | The rail is already six items; web-style browsing still needs dedicated list/detail views. |
| 2 | **Runtime config:** P4.1 (server URL + token entry) is a prerequisite for Discover and AI DJ. Existing `BuildConfig` values remain the default/fallback. | Both features are server-backed and currently require an APK rebuild to change servers. |
| 3 | **AI DJ v1:** text brief only. Voice recording/transcription is deferred to a follow-up (P4.3b). | Delivers the core set flow without microphone permissions and WAV capture complexity. |
| 4 | **Cross-device:** start with resume-spot push/pull + “Continue on this device” (MVP). Full SignalR remote control is optional later. | Resume is the highest-value cross-device behaviour and avoids mirroring the whole player state machine. |
| 5 | **Accounts:** Web login first, scoped `hth_` API key under Advanced. Login uses the same account as the browser; account/API-key management stays web-only. | Reuses the server auth system and keeps the existing key-based installs working. |
| 6 | **Video preview:** audio-only preview on mobile (existing `PreviewPlayer`). No embedded YouTube WebView. | Simpler, battery-friendly and matches the current Download screen; mark as a deliberate deviation. |
| 7 | **Move to Songs:** local file move + Room updates + tombstones; do not depend on the web server endpoint. | Keeps the feature working offline and consistent with mobile's on-device architecture. |
| 8 | **Metadata while playing:** pending edit applied on the next track change (implemented in P1.1). | Avoids rewriting tags on a file the player currently holds open. |

---

## 5. Definition of done (per item)

- Feature is reachable from a normal UI path (no dead code).
- Persists locally and participates in sync where the data is synced.
- Handles empty/error/offline states without crashing.
- Has at least one automated test at the repository or Compose level.
- README/docs updated if behaviour or setup changes.

---

## 6. Progress log

| Date | Item | Notes |
|---|---|---|
| 2026-11-14 | **P0.1 — volume** | `PlayerManager.setVolume()` applies/persists the user level, survives track changes and scales crossfade gains; the Now Playing slider is the single volume control. `CrossfadeGainTest` added. |
| 2026-11-14 | **P1.1 — metadata editor** | “Edit info” wired into All Songs, Daily Mix, Home strips and playlist detail; the dialog provides tags, cover, iTunes lookup, delete and playlist checkboxes. Edits to the playing file are queued and applied on the next track change. `PendingMetadataEditsTest` added. |
| 2026-11-14 | **P0.2 — dead code** | Removed `LibraryScreen`/artist-album grids/detail views; kept `SortHeader` + `EditSongDialog` in `ui/SongComponents.kt`. |
| 2026-11-14 | **P0.3 — decisions** | Navigation, runtime config, AI DJ scope, cross-device MVP, accounts, video preview, Move to Songs and pending-edit decisions recorded in §4. |
| 2026-11-14 | **P1.2 — playlists** | Standalone “Add to playlists” dialog (immediate toggles, inline create+assign) wired to all song menus; repository test added. |
| 2026-11-14 | **P1.3 — playlist covers** | Remove-cover action + 800 px JPEG downscale before storing the data URI; cover helper tests added. |
| 2026-11-14 | **P2.1 — artists/albums** | `LibraryBrowseScreen.kt` with list/detail, search, lazy artwork, play-all/shuffle, song menus; Home cards + hidden `Dest` entries. |
| 2026-11-14 | **P2.2 — Home cards** | Artists, Albums and Podcasts cards added; Podcasts card shows count and plays the first episode. |
| 2026-11-14 | **P2.3 — History** | Covers, active state, long-press/overflow menus, edit/playlists, full-tab queue context and menu-based re-download. |
| 2026-11-14 | **P2.4 — Move to Songs** | `PodcastRepository.moveToSong()` + confirmation dialog; local file/room move with podcast/chapter tombstones. |
| 2026-11-14 | **P3.1 — fuzzy search** | `FuzzySearch` ported with diacritics/token/Damerau matching; wired into all search boxes; tests added. |
| 2026-11-14 | **P3.2 — lyrics extras** | Delete, local kana Romaji fallback, chapter selector + chapter-keyed lyrics/offsets with rebased highlighting. |
| 2026-11-14 | **Lyrics full-screen** | Replaced the nested lyrics drawer with a dedicated full-screen view (web parity): large centered lines, active-line auto-follow, tap-to-seek, timing offset, romaji, search/replace, edit and a compact transport. Search now uses the Web API `/lyrics/search` (same cleaning/lrclib filters as web) with a filtered lrclib fallback and explicit loading state. New lyrics entry point on the player bar. |
| 2026-11-14 | **P3.3 — download extras** | `.txt` batch import, owned-copy confirmation, audio-only preview kept; parser tests added. |
| 2026-11-14 | **P4.1 — runtime Web API** | URL/API-key fields persisted in `SyncConfig`, Settings Save/Test, BuildConfig fallback; no login. |
| 2026-11-14 | **P4.2 — Discover** | Web API feed/filters/preview/dismiss/feedback + on-device download and owned guard. |
| 2026-11-14 | **Discover preview fix** | Previews now use server YouTube search + resolved audio URL (same as web), try multiple candidates, show Finding/Playing/Failed state and a mini player, support cover-play, and surface errors instead of silently staying highlighted. Downloads search via the server first. |
| 2026-11-14 | **P4.3 — AI DJ** | Web API state/start/stop/feedback + local batch playback and commentary display (voice/auto-advance deferred). |
| 2026-11-14 | **P4.4 — cross-device resume** | Pause pushes a spot to `/sync/playback`; Home shows a “Continue on …” card that restores queue + position. |
| 2026-11-14 | **P4.5 — accounts** | Originally dropped; superseded on 2026-11-23 by Web account login with the API key kept as an advanced fallback. |
| 2026-11-14 | **Test runner** | `scripts/test.ps1` now tolerates Gradle/adb stderr warnings, so it no longer aborts on the SDK XML warning. |
| 2026-11-16 | **P4.6 — playback telemetry** | Mobile posts one completion/skip segment per non-podcast track; pause/resume stays one segment, repeat/track change/queue end flush. `PlaybackTelemetryTest` + `PlaybackEventsApiTest` added. |
| 2026-11-16 | **P4.7 — automatic history sync** | Durable history outbox + debounced scoped import; natural-key pull dedupe and id-collision re-key. `HistorySyncOutboxTest` added; Room v10 migration. |
| 2026-11-16 | **Live pipeline validation** | Deployed server confirmed natural-key dedupe and desktop outbox push/clear; Settings Save now flushes pending history. |
| 2026-11-16 | **Live mobile playback** | AVD: played a real MP3 from All Songs; server received the history row automatically, and Next sent a skipped playback event (insights 60→61). Instrumentation suite: 6/6 passed. |
| 2026-11-23 | **Web account login** | `AuthSession` signs in through `/auth/login`, keeps access JWTs in memory, stores the rotating refresh token encrypted by an Android Keystore AES/GCM key, refreshes single-flight and retries 401s. Settings has sign-in/out, a plain-HTTP warning, and the legacy API-key fallback. `AuthSessionTest` added. |
| 2026-11-23 | **P4.8 — auth edge cases** | Sync JSON/file uploads/downloads, app-update checks/APK downloads, and AI DJ clip URLs refresh once on 401 with the failed token; offline/5xx keeps the session. `SyncApi` and `UpdateHttpApi` retry tests added. |
| 2026-11-23 | **Release** | Android `0.4.24-hathor` (27) published with both auth modes; auth edge-case fixes in `0.4.25-hathor` (28). |
| 2026-11-23 | **GitHub update source** | Mobile `0.4.26` and desktop `1.0.3` read public GitHub Releases (`android-v*` / `desktop-v*`, dev channel), pick the highest `versionCode`, and verify the asset digest/SHA256SUMS. The old server/SMB channel remains for older clients. |
| 2026-11-23 | **Dedicated login screens** | Desktop gets the web-styled full-screen gate; Android gets a matching Compose gate with the app logo. Both add show/hide password, HTTP risk acknowledgement, Keystore/keyring token storage, and move sign-in out of Settings. |
