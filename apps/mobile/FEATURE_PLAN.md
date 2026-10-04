# Hathor Android — full feature-parity plan (visual style kept)

Desktop source of truth: `Music Player/ui/index.html` (+`views/`, `modals/`),
`services/*.py`, `api.py`, `Download.py`, `settings.py`, `sync.py`.
App name on both: **Hathor**. This doc is the build order. Nothing here is
aspirational — every line maps to a desktop behavior observed in source.

---

## 1. Visual system (sampled from the desktop CSS, not invented)

| Token | Desktop value | Android mapping |
|---|---|---|
| Background | `bg-zinc-950` (#09090B) | `HathorColors.Background = 0xFF09090B` |
| Surface / glass | `bg-black/40 + backdrop-blur-2xl + border-white/5` | translucent black surface (0x66000000) over background, 1dp `White5` border (0x0DFFFFFF), 16–24dp radius |
| Accent | `orange-500` #F97316, highlights `orange-400` #FB923C, deep `orange-600` #EA580C | `Accent / AccentBright / AccentDeep`, gradient `orange-400→orange-600` for brand tile + headers |
| Text | `zinc-100/300` titles/body, `zinc-500` hints | `TextPrimary 0xFFF4F4F5`, `TextBody 0xFFD4D4D8`, `TextHint 0xFF71717A` |
| Active nav | `bg-orange-500/10` + 4dp left indicator + orange icon | same: 10% orange fill, 4dp indicator bar, orange icon |
| Ambient glows | two huge blurred orange circles, 10% alpha | two radial-gradient blobs behind content (10% alpha) |
| Cards/headers | `rounded-2xl`, icon tile `w-12 h-12 rounded-xl bg-orange-500/10 border-orange-500/20` | `HeaderIconTile` composable reused on every screen |
| Dialogs | SweetAlert2 glass: dark blur, orange confirm | Material `AlertDialog` styled dark w/ orange confirm button |
| Toasts | Notyf | Snackbar w/ dark + orange styling (no Notyf dep needed) |

## 2. Screen map (desktop view → Android destination)

| Desktop | Android | Status |
|---|---|---|
| Sidebar (Home/Download/Playlists+submenu/History/Settings, collapsible) | `NavigationRail` (portrait) / drawer, same items + brand tile, collapse→icons | THIS TURN (shell) |
| Bottom player bar (cover, title/artist, shuffle/prev/play/next/repeat, progress, volume) | Bottom bar, same layout; volume→system volume on mobile | THIS TURN (bar + minimal local playback) |
| Queue sidebar | Queue bottom-sheet from player bar | playback phase |
| `home` (search + song list, artist/album views) | Library screen restyled into this shell | restyle this turn |
| `download_songs` (search + download + jobs) | Download screen (engine test merges in) | restyle this turn |
| `playlist_home` + `playlist_view` + `add_playlist` modal | Playlists screens (Room-backed, CRUD) | next |
| `history` (download + played tabs) | History screen (Room `Music_History` + `Download_Queue`-equivalent) | next |
| `settings` (volume, limit, background, songs path, browser) | Settings screen (volume, download concurrency, SAF folder; no window/browser settings) | next |
| `edit_song` modal | already built (LibraryScreen dialog) → restyle | restyle this turn |
| Lyrics overlay (`lyrics-container-view`, lrclib) | Lyrics bottom-sheet (lrclib get/search, romanize skipped*) | lyrics phase |
| Artist/album images (iTunes Search API) | Coil + iTunes Search API, cached | polish phase |

\* `pykakasi` romanization has no maintained Android port — port as plain text,
note the gap openly instead of faking it.

Desktop-only (dropped, listed so nobody re-adds): frameless titlebar + win
controls, resize zones, `browser` (yt-dlp browser-cookie) setting, `%APPDATA%`
path, pygame, pywebview bridge, Notyf/SweetAlert2/Alpine deps.

## 3. Service map (desktop → Android)

| Desktop | Android | Notes |
|---|---|---|
| `Download.py` (yt-dlp + ffmpeg) | youtubedl-android + bundled ffmpeg | DONE (Phase 1) |
| `sync.py` + `sync_remote_to_local_and_download` (pymysql→SQLite) | Connector/J → Room, same tables/columns | DONE (Phase 2) |
| `metadata.py` (mutagen) | MediaMetadataRetriever (read) + jaudiotagger-android (write) | DONE (Phase 3) |
| `playback.py` (pygame queue) | `android.media.MediaPlayer` now → Media3 later | THIS TURN minimal: play/pause/next/prev/seek, library order |
| `downloads.py` (DownloadManager, concurrency, DB jobs) | WorkManager or coroutine queue + Room `Download_Queue` table | download phase (Phase 2 enqueue is sequential; manager adds retry/limit) |
| `lyrics.py` (lrclib) | OkHttp/Ktor → lrclib get/search + save to Room `Lyrics` | lyrics phase |
| `apple.py` (iTunes artwork) | iTunes Search API + Coil | polish phase |
| `windows_media.py` (SMTC overlay) | MediaSession + notification | playback phase |
| `settings.py` + Settings table | DataStore + Room Settings row (volume, limit_downloads, songs path URI) | settings phase |

## 4. Build order (each step = green APK)

1. **Shell + theme + minimal playback (THIS TURN):** `HathorTheme`, shell with
   rail + bottom bar, restyle Library/Download/Sync into it, `PlayerService`
   (MediaPlayer): play/pause/next/prev/seek over on-device mp3s.
   Pass = APK builds; bar plays a synced mp3 end-to-end.
2. **Playlists:** list/create/rename/delete + add/remove songs + submenu counts
   (Room tables exist from Phase 2).
3. **History + Settings:** played/download history tabs; volume, concurrency
   limit, SAF folder picker (replaces songs-path text field).
4. **Downloads manager:** concurrent queue w/ limit, retry, persistent jobs.
5. **Queue + lyrics + NowPlaying:** queue sheet, lrclib sheet, MediaSession.
6. **Polish:** iTunes artwork, artist/album views, search across library.

## 5. Mobile adaptations (explicit, not silent)

- No titlebar/window controls/resize — edge-to-edge + system bars.
- Songs live in app-private Music dir now; SAF picker (Phase 3 of old plan)
  becomes the Settings "music folder" row.
- Volume slider drives the system music stream, not pygame.
- Background sync loop (desktop 30s thread) becomes WorkManager periodic task.
- TLS: Android system CA store replaces `certifi`.
