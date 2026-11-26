# Usage Guide

Hathor is a single-window desktop app. The left rail switches between views: **Home**, **Songs**, **Download**, **Discover**, **Artists**, **Albums**, **Podcasts**, **Playlists**, **History**, **AI DJ**, and **Settings**. Clicking an artist or album name anywhere opens its dedicated page. Discover and AI DJ need the **Settings → Web API** server/key configuration; remaining parity work is tracked in [WEB_PARITY.md](../WEB_PARITY.md).

## Home dashboard

Home is a dashboard, not a song list:

- **All Songs card** — opens the full library (searchable, sortable). Clicking a song queues the rest of the visible list from that point on.
- **Daily Mix card** — opens your 50-song mix of the day (see below). Its play button starts the mix with a `daily_mix` queue source.
- **AI DJ card** — shows Vohithe's ready/on-air state and starts a new set without leaving Home.
- **Podcasts card** — shows the episode count and play-first button, queuing the podcast library.
- **Recently Played / Recently Downloaded** — horizontal strips. Clicking an episode/song plays it with the strip as its queue. The currently playing item is highlighted in the strip.
- Each card shows live counts (library size, today's mix size/date, podcast episodes).

## Daily Mix

A fresh 50-song mix built every day from your listening history (`Music_History` play counts):

- 2 songs from your top 10 most-played,
- 5 from ranks 11–25,
- 13 from ranks 26–70,
- the remaining 30 prefer songs outside your top 70 (discoveries/long tail).

The mix is generated once per day and cached, so it stays stable if you restart. Small libraries just get fewer songs; if the mix can't be built (no songs yet), the view explains what to do.

## AI DJ (Vohithe)

Vohithe is the server-planned private radio. Configure **Settings → Web API** with a key that has `player:read` + `player:control` first.

- **Start** — from the Home **AI DJ** card, the **AI DJ** page, or the sparkles button in the player bar. The vibe composer accepts a typed brief; **Speak** records short microphone windows and appends the transcription.
- **Sets** — the server sends 3–4 local-library tracks per batch. The page shows the current track and set, next-set readiness, voice state and a live visualizer. Upcoming tracks/queue are hidden while the DJ is on air.
- **Voice** — Vohithe's outro/intro clips play through a separate webview voice bus while the music is ducked, then the next batch starts. If clips are not ready at the boundary, the transition waits briefly and retries before continuing.
- **Controls** — **Stop** ends the session and leaves the current music queue playing; **New set** starts a fresh batch request. **More like this / Less like this** send feedback.
- **Restart/leave the page** — the DJ keeps running while you navigate; the nav label and Home card show **On air**.
- **Server queue** — the desktop mirrors its local batch queue to the server one forward track at a time so the server can prefetch the next set; starting a normal queue ends DJ mode.

## Discover

Discover shows out-of-library recommendations from the Hathor server. Open **Settings → Web API** first and use a key with `library:read` + `library:write`.

- **Cards** — artwork, source badge/reason, album/year and dismiss (`×`). Source chips filter by Similar tracks, Related artists, Genres, Popular, From your artists, Trending or Picked for you; the search box filters the visible list.
- **Play all / cover play** — starts the preview queue (first 20 visible suggestions), pauses the main player, and keeps playing while you change views. The dock above the player bar has prev/next, play/pause, queue statuses, seek and Stop.
- **Video (top-right play)** — resolves the first YouTube hit and opens an embedded preview; videos that refuse embedding still offer **Watch on YouTube**.
- **Download (bottom-right)** — checks the local library first and asks before creating a duplicate. Progress appears as a percentage pill on the card; downloads run through the normal local queue.
- **Refresh** — asks the server for a fresh daily ranking. **Your signal** (when feedback exists) shows conversion/playback/download stats and can tune the server weights.
- **Playback signal** — one completion/skip event per music track is sent to the Web API when configured; podcasts are skipped, and telemetry failures never affect playback.
- Starting any main song or resuming playback stops the preview; previews pause the main player when they start.

## All Songs

The main library view lists the MP3 files in the songs folder (`%appdata%\musicPlayer` by default).

- **Play a song** — click it. The queue is populated with the rest of the (visible, i.e. search-filtered) list from that point on.
- **Search** — typo-tolerant matching across title, artist and album; diacritics are ignored ("beyonce" finds "Beyoncé").
- **Queue** — the panel shows upcoming tracks. You can jump to a queue position, remove entries, and reorder them.
- **Play next / Add to queue** — available on each song so you can build a queue without disturbing the current one.
- **Shuffle / Repeat** — toggles in the player bar; shuffle keeps a separate un-shuffled order so repeat/follow on music stays logical. Artist and album detail pages also have a dedicated shuffle-all button.
- **Progress & volume** — seek with the progress slider; the volume slider persists across sessions.
- **Edit metadata** — opens a modal to change title, artist, album, year, and genre, with optional cover art replacement. Metadata is written to the MP3 via mutagen. Editing the currently playing file is held as a **pending edit** and applied when the track changes (or on close), so the player never rewrites a loaded file. A failed OS overlay update can never fail the save itself.
- **Search iTunes metadata** — fetch a proposed title/artist/album/cover from the iTunes Search API (single result or multiple candidates to choose from).
- **Move to Podcasts** — available in the song context menu. The file moves to the podcast folder; playlist links, lyrics and music history for that file are removed and tombstoned so sync cannot resurrect it as a song.
- **Delete** — removes the file from the library and the `Songs` table. Deleting the current track stops it and safely advances to the next queued track.
- **Right-click any song** — opens the same context menu as the ⋮ button, anywhere songs are listed (library, mixes, playlists, artist/album pages, history, queue).

> The app only plays `.mp3` files. Drop files into the songs folder and they appear on the next library refresh.

## Podcasts / non-music audio

A separate library for podcasts and other spoken audio, kept fully apart from music:

- **Separate folder** – `%appdata%\musicPlayerPodcasts` by default (changeable in Settings). Files here never show up in All Songs, Daily Mix, or Recently Played.
- **Downloading** – the Download view has a **Songs / Podcast-Audio** toggle. Podcast downloads skip the iTunes song-match (which would mangle episode names) and keep the uploader's title/author instead. Failed podcast downloads retry as podcasts.
- **Listing is lightweight** – the view shows filenames/DB titles and dates only. Full metadata (duration, cover) loads on demand: **Get Metadata** in the episode menu, automatically when you press play, or via **Edit Info**.
- **Episode menu** (⋮ button or right-click) — Play, Play Next, Add to Queue, Get Metadata, Edit Info, Tags…, Chapters…, Move to Songs, Delete.
- **Move to Songs** — moves the file to the songs folder; podcast tags and chapter marks for that file are removed and tombstoned, while the audio file and tags stay intact.
- **Search** — typo-tolerant title/artist matching, with tag filter chips still applied.
- **Pending edits** — editing the currently playing episode uses the same pending-edit flow as songs (applies on track change/close).
- **Chapters** – per-episode chapter marks (name + start, optional end; `mm:ss` input). Click a chapter's time chip to jump there. With **Chapter auto-skip** on (Settings), episodes start at the first chapter and skip to the next chapter when one ends; open-ended chapters never auto-jump.
- **No playlists, but queues merge** – episodes queue among themselves, and Play Next / Add to Queue work in both directions, so one queue can mix songs and episodes. Episode plays don't pollute music history, Daily Mix, or Recently Played.
- **Editing** – Edit Info opens the shared metadata modal and writes tags to the episode file plus the `Podcasts` table.
- **Sync** – podcasts sync both ways with the server like songs do (see [Database & sync](DATABASE.md)).

## Now Playing & shortcuts

- Click the mini-player to open the **fullscreen Now Playing** view (large art, full controls, volume).
- **Keyboard** – `Space` play/pause, `←`/`→` seek ±10 s, `↑`/`↓` volume, media keys work via the OS integration below.
- **Restart resume** — Hathor remembers the current song *and where it was playing from* (playlist, daily mix, artist, album, recents, podcast). On launch the queue is rebuilt from that context; a customized queue is restored verbatim. Stale contexts fall back gracefully (e.g. yesterday's daily mix → today's mix if the song is still in it, otherwise the general library).
- **Live cross-device playback** — with the Web API configured, desktop polls every 3 s and converges pause/resume/next/seek/volume with web/other clients. Tracks missing locally are skipped with a “Not on this device” notice. Local AI DJ sessions remain authoritative.
- **Resume from another device** — the player-bar dots open the foreign paused spot with its device, age, song and saved position; a matching banner appears inside Now Playing. Resuming restores the sender's Up Next queue and position. Nothing auto-plays, and a resumed spot stays dismissed for that device snapshot.

## Playlists

- **New playlist** — name + optional description + optional cover image (downscaled to ≤800 px JPEG before storage).
- **Add songs** — from any song, pick one or more playlists to add it to.
- **Open a playlist** — shows its songs; playing one queues the rest of the playlist.
- **Edit / Delete** — rename, change description/cover, remove the cover, or delete. Deletion is recorded for server sync (see [Database & sync](DATABASE.md)).

## Download Songs

- **Search** – type a song name; results come straight from YouTube with title, uploader, duration, and thumbnail.
- **Songs / Podcast-Audio toggle** – downloads land in the music library or the podcasts library respectively (own folder, own table).
- **Download** — appended an `"audio"` qualifier and downloads the best audio, converting to **320 kbps MP3** via FFmpeg.
- **Owned guard** — before queueing, Hathor checks the selected library (Songs or Podcasts) with the Discover title/artist normalizer. If it is already owned, it asks **Already in your library — download again?**.
- **Progress** — per-download progress bar updated by `update_download_progress` (0 → 90 % download, 95 % metadata processing, 100 % done).
- **Automatic tagging** — after a *song* download, Hathor queries the iTunes Search API and writes ID3 tags plus 600×600 cover art, then renames the file to `Title.mp3` (avoiding collisions with `Title (1).mp3`, etc.). Podcast downloads skip this and keep the uploader's title/author.
- **Metadata fallback** — if iTunes lookup fails, the yt-dlp title/uploader are used.
- **Batch import** — import a `.txt` file with one query per line; downloads run with the configured concurrency limit and respect the Songs/Podcast toggle. Owned entries are skipped, and the completion toast reports **queued / already in library / failed** counts.

## Lyrics

The lyrics panel shows the current song's lyrics.

- **Synced** – LRC-style timestamps are translated into per-line highlighting as the song plays.
- **Plain** – full-text lyrics if no timed version is available.
- **Local cache** – fetched lyrics are stored in the `Lyrics` table so they work offline.
- **Unknown artists** — LRCLIB's exact endpoint requires an artist (it answers those requests with HTTP 400), so artist-less songs fall back to a track-only search that is only accepted on an exact track-name match.
- **Search / Replace** — the **Search** / **Replace** button opens a panel prefilled from the song (or the active podcast chapter, split into title/artist). It asks the Web API `/lyrics/search` first (when configured) and falls back to lrclib. Candidates show track, artist, album, duration and synced/plain availability; you can preview, save/replace, or remove the saved row.
- **Chapter targets** — for podcasts with chapters, the panel's **Save to chapter** picker defaults to the active chapter and also offers the whole episode. Saving to a different chapter shows a jump banner.
- **Romanization** — raw lyrics are shown by default. The **Romaji** toggle romanizes on demand via the Web API, with local `pykakasi` fallback, without changing the cached lyrics.
- **Highlight timing correction** — if the synced highlighting runs early/late, nudge it with the −1s/−.1s/+.1s/+1s stepper in the lyrics header. Persisted per song (±20 s max) in the `Lyrics` table, so it survives restarts and syncs to other devices.

## History

Two paginated lists:

- **Downloads** — every song downloaded (from the `Songs.date_download` column).
- **Played** — songs and playlists you've played (`Music_History` and `Playlist_History`), one entry per play event, latest first.
- **Rows** — clicking a row queues the whole active tab, not the general library (download history uses `download_history`; played uses `recently_played`). Next/previous stay inside that tab.
- **Actions** — the ⋮ button or right-click opens the global song menu (Play, Play Next, Add to Queue, Edit Info, Add to Playlists, Delete); download rows also keep a Re-download button. Deleting the current track advances safely.

## Settings

- **Audios row** – click the folder path or **Change** to pick the songs folder. **Push** scans that folder into the DB, then uploads songs, playlists, lyrics, and history to the server. **Pull** downloads the audio delta and any missing files.
- **Podcasts row** – click the folder path or **Change** to pick the podcasts folder. **Push** rescans podcasts, then uploads episodes, tags, and chapters. **Pull** fetches the podcast delta and missing files.
- **Background** – pick a custom background image or remove it.
- **Download limit** – maximum simultaneous downloads.
- **Volume** – default playback volume.
- **Crossfade** – enable/disable overlap between consecutive tracks (1–12 s, equal-power curve). Applies to automatic transitions; manual next/previous stay instant. Track handoff is gapless whether crossfade is on or off.

## Server sync (manual)

There is **no background auto-sync**. Each library row has its own **Push** and **Pull**, scoped to that library only:

- **Push** – scans the selected folder first (no separate rescan button), then uploads that library's rows plus new file bytes. Pending deletions for the other library are left untouched.
- **Pull** – downloads that library's incremental delta into SQLite and fetches missing files straight from the server (audios → songs folder, podcasts → podcasts folder). Pulling also adopts the server daily mix of the day.
- Each library keeps its own delta cursor, so pushing/pulling one never skips changes in the other.

## Windows media keys / overlay

Hathor registers **System Media Transport Controls** via `winsdk` (see `services/windows_media.py`). Media keys (Play/Pause, Next, Previous) on your keyboard control playback, the OS media overlay shows the song title/artist/album art, and its press status stays in sync with the app's play state.

## First-run server sync

After you sign in on the dedicated login screen, the first launch asks whether to load the library from the sync server. Choosing **"Yes, load from server"**:

1. Pulls songs, podcasts, playlists, lyrics, daily mix, and history down into the local SQLite DB.
2. Downloads any songs/podcasts whose files aren't present locally, straight from the server (each into its own folder).

Afterwards, sync stays fully manual via the Settings buttons above.