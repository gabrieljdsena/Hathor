import { create } from 'zustand'
import { api, deviceId, deviceLabel, type PlayerState, type QueueSource, type Song } from '../api/client'
import { recordEndedPlayback, startPlaybackTelemetry } from '../audio/telemetry'
import { engine, type EngineTrack } from '../audio/engine'

// Client playback store. Server PlaybackState is the source of truth
// (pulled on load); the AudioEngine renders it with gapless handoff and
// equal-power crossfade. Fades never move server state except at the fade
// start, when the engine asks the store to advance (api.next) exactly as
// the new audio starts — server position and new track stay aligned.
// Snapshot this browser's paused spot for other devices (fire-and-forget,
// never blocks playback). Only when paused with a track loaded — playing
// states are never pushed (a live position would clobber a genuine paused
// spot elsewhere). engine.time() is the media-clock truth at push moment.
// The snapshot carries this device's Up Next (current track first, capped
// at 200) plus its source so the other side restores the foreign queue.
export function pushResumeSpot(): void {
  try {
    const s = usePlayer.getState()
    const song = s.currentSong
    if (!song || s.isPlaying) return
    const seen = new Set<string>()
    const queueFiles: string[] = []
    for (const f of [song.file, ...s.queue.map((q) => q.file)]) {
      if (!f || seen.has(f.toLowerCase())) continue
      seen.add(f.toLowerCase())
      queueFiles.push(f)
      if (queueFiles.length >= 200) break
    }
    void api
      .pushPlayback(deviceId(), {
        file: song.file,
        positionSec: engine.time(),
        isPodcast: !!song.isPodcast,
        deviceLabel: deviceLabel(),
        queueFiles,
        queueSource: s.source,
      })
      .catch(() => {})
  } catch {
    /* resume sync must never break playback */
  }
}

interface PlayerStore extends PlayerState {
  refresh: () => Promise<void>
  boot: () => Promise<void>
  playSong: (song: Song, context: Song[], source: QueueSource | null) => Promise<void>
  // Infinite-scroll play flow: the full Song[] list is never materialized —
  // only ordered file names (see the */files endpoints). Same transport
  // semantics as playSong.
  playSongByFiles: (song: Song, files: string[], source: QueueSource | null) => Promise<void>
  toggle: () => Promise<void>
  skipNext: () => Promise<void>
  skipPrev: () => Promise<void>
  toggleRepeat: () => Promise<void>
  syncAudio: () => void
  // Volume normalization (client-side leveling): browser-local only, never
  // synced — no server setting exists for it (unlike volume/crossfade).
  normalize: boolean
  setNormalize: (enabled: boolean) => void
  // Podcast chapter auto-skip: when a chapter plays to its end time, jump
  // to the next chapter. Browser-local like normalize (no server setting);
  // the transport arrows jump chapter boundaries regardless of this flag.
  chapterSkip: boolean
  setChapterSkip: (enabled: boolean) => void
  // Audio is (re)loading/stalled — surfaced instead of looking frozen.
  buffering: boolean
  // A pause/resume command is retrying against a flaky API.
  syncing: boolean
}

const NORMALIZE_KEY = 'hathor:normalize'
const CHAPTER_SKIP_KEY = 'hathor:chapterskip'

function loadFlag(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}

function loadNormalize(): boolean {
  return loadFlag(NORMALIZE_KEY)
}

function loadChapterSkip(): boolean {
  return loadFlag(CHAPTER_SKIP_KEY)
}

function newInstanceId(): string {
  return (
    globalThis.crypto?.randomUUID?.() ??
    `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
  )
}

function toTrack(song: Song) {
  return {
    file: song.file,
    url: api.streamUrl(song.file, song.isPodcast),
    isPodcast: !!song.isPodcast,
    gainDb:
      song.loudnessDb === null || song.loudnessDb === undefined
        ? null
        : -14 - song.loudnessDb,
  }
}

export function nextForFade(): EngineTrack | null {
  const state = usePlayer.getState()
  if (state.repeat) return null
  const next = state.queue[0]
  return next ? toTrack(next) : null
}

// Transport generation: every mutating call (playSong, toggle) claims a
// number up front; each awaited response applies only while still current.
// Rapid clicks otherwise interleave (a cover burst can stall fetches for
// seconds behind the 6-connection pool) and a STALE response reloads an old
// track over the new one — or resumes audio right after a pause. Fresh
// reads (refresh, boot) always apply: they pull latest server truth, so the
// UI can never stick on a dropped generation.
//
// Hub broadcasts are the exception with the opposite rule (see playerHub):
// while OUR OWN flight is in progress they are skipped — a sibling click's
// server state landing mid-flight would revert us just like a stale REST
// response. Our flight's own responses decide the outcome instead.
let transportGen = 0
let inflightTransports = 0
let repeatGen = 0
function claimTransport(): number {
  transportGen += 1
  inflightTransports += 1
  return transportGen
}
function releaseTransport(): void {
  inflightTransports = Math.max(0, inflightTransports - 1)
}
export function hasInflightTransport(): boolean {
  return inflightTransports > 0
}
function isCurrentTransport(gen: number): boolean {
  return gen === transportGen
}

// The engine renders whatever the server says is current (used after every
// server round trip and remote-style mutation). Fade-aware: while a fade
// already renders the server's track (server advances at fade start), only
// play/pause + volume are synced — never reload, or the fade restarts.
function syncEngine(state: PlayerState) {
  const song = state.currentSong
  const file = song?.file ?? null
  if (engine.isFading() && (file === engine.currentFile() || file === engine.fadeTarget())) {
    if (state.isPlaying && !engine.isPlaying()) engine.play()
    else if (!state.isPlaying && engine.isPlaying()) engine.pause()
    engine.setVolume(state.volume)
    return
  }
  if (engine.currentFile() !== file) {
    engine.load(song ? toTrack(song) : null, state.isPlaying)
  } else {
    // Repeat-one wrap lands on an ended element, where play() is a no-op:
    // rewind first, then (re)start. Only when repeat is on — otherwise a
    // response arriving while the element parks at the edge would restart
    // a finished song instead of letting `ended` advance the queue.
    const dur = engine.duration()
    if (state.repeat && dur > 0 && state.isPlaying && engine.time() >= dur - 0.25) engine.seek(0)
    if (state.isPlaying && !engine.isPlaying()) engine.play()
    else if (!state.isPlaying && engine.isPlaying()) engine.pause()
  }
  engine.setVolume(state.volume)
  // Snap the element to the server on real drift only (refresh resume,
  // remote seek, repeat-one wrap) — small drift is left alone so
  // volume/shuffle responses never cause audible jumps. Never fight a fade.
  // driftTarget owns both caps: the duration edge (a wall-clock estimate
  // that outran a stalled element must never land on the edge — it fires
  // `ended` and advances away mid-track) and the recovery ceiling (after a
  // far-seek stall the server still names the poisoned target; jumping back
  // into it loops until the budget is exhausted and the bar freezes with a
  // dead toggle). System seek: never mints fresh recovery budget.
  if (!engine.isFading()) {
    const target = engine.driftTarget(state.positionSec, song?.duration ?? 0)
    if (target !== null) engine.seek(target, false)
  }
}

// ---- reliable pause/resume intents ----------------------------------
// A blind toggle is unsafe to retry: a lost response would flip the server
// back. Pause and resume are explicit and idempotent, so a failed command
// keeps the optimistic UI and retries with backoff until acknowledged. This
// is what prevents "paused visually but the backend kept playing" and the
// resulting track change on unpause.
interface PlaybackIntent {
  gen: number
  playing: boolean
  file: string | null
  positionSec: number | null
  attempts: number
  timer: ReturnType<typeof setTimeout> | null
}
const INTENT_RETRY_BASE_MS = 500
const INTENT_RETRY_MAX_MS = 30000
let playbackIntent: PlaybackIntent | null = null
// Requests are serialized so a canceled command that is already on the wire
// can never land after (and undo) the intent that replaced it.
let intentQueue: Promise<void> = Promise.resolve()

export function hasPendingPlaybackIntent(): boolean {
  return playbackIntent !== null
}

function isCurrentIntent(intent: PlaybackIntent): boolean {
  return playbackIntent === intent && intent.gen === transportGen
}

function finishPlaybackIntent(intent: PlaybackIntent) {
  if (playbackIntent !== intent) return
  if (intent.timer !== null) {
    clearTimeout(intent.timer)
    intent.timer = null
  }
  playbackIntent = null
  releaseTransport()
  usePlayer.setState({ syncing: false })
}

export function cancelPlaybackIntent() {
  if (playbackIntent) finishPlaybackIntent(playbackIntent)
}

function intentRetryDelay(attempt: number, error: unknown): number {
  const message = error instanceof Error ? error.message : String(error)
  const hinted = /retry in (\d+)s/i.exec(message)
  if (hinted) return Math.min(INTENT_RETRY_MAX_MS, Number(hinted[1]) * 1000)
  return Math.min(INTENT_RETRY_MAX_MS, INTENT_RETRY_BASE_MS * 2 ** Math.min(attempt, 6))
}

// Auth/validation failures will never succeed by retrying. Everything else
// (network drop, timeout, 5xx, 429) stays queued.
function isPermanentIntentError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error)
  return /unauthorized|forbidden|not found|bad request|\b400\b|\b401\b|\b403\b|\b404\b/i.test(message)
}

async function attemptPlaybackIntent(intent: PlaybackIntent): Promise<void> {
  if (!isCurrentIntent(intent)) return
  const previous = intentQueue
  let release!: () => void
  intentQueue = new Promise<void>((resolve) => {
    release = resolve
  })
  try {
    await previous
    if (!isCurrentIntent(intent)) return
    const state = intent.playing
      ? await api.resume()
      : await api.pause(intent.positionSec, intent.file)
    if (!isCurrentIntent(intent)) return
    finishPlaybackIntent(intent)
    usePlayer.setState(state)
    syncEngine(state)
    if (!state.isPlaying && state.currentSong) pushResumeSpot()
  } catch (e) {
    if (!isCurrentIntent(intent)) return
    if (isPermanentIntentError(e)) {
      finishPlaybackIntent(intent)
      void usePlayer.getState().refresh().catch(() => {})
      return
    }
    const delay = intentRetryDelay(intent.attempts, e)
    intent.attempts += 1
    if (intent.timer !== null) clearTimeout(intent.timer)
    intent.timer = setTimeout(() => {
      intent.timer = null
      if (isCurrentIntent(intent)) void attemptPlaybackIntent(intent)
    }, delay)
  } finally {
    release()
  }
}

function startPlaybackIntent(
  playing: boolean,
  file: string | null,
  positionSec: number | null,
): PlaybackIntent {
  cancelPlaybackIntent()
  const gen = claimTransport()
  const intent: PlaybackIntent = { gen, playing, file, positionSec, attempts: 0, timer: null }
  playbackIntent = intent
  usePlayer.setState({ syncing: true })
  return intent
}

function retryPendingIntentNow() {
  if (!playbackIntent) return
  if (playbackIntent.timer !== null) {
    clearTimeout(playbackIntent.timer)
    playbackIntent.timer = null
  }
  void attemptPlaybackIntent(playbackIntent)
}

// Coming back online should not wait out a 30s backoff (transport command
// and audio stream alike).
if (typeof window !== 'undefined') {
  window.addEventListener('online', () => {
    retryPendingIntentNow()
    engine.retryNow()
  })
}

// Server advance at fade start (engine calls this exactly when new audio
// starts, passing the outgoing file). syncEngine is fade-aware, so the
// matching case never reloads; a changed queue (or stop) hard-switches via
// the normal path. The outgoing file makes the advance conditional: a
// manual transport in the preload/fade window already consumed it, so the
// server converges instead of skipping a second song.
async function advanceForFade(outFile: string | null) {
  const state = await api.next(outFile)
  usePlayer.setState(state)
  syncEngine(state)
  // A fade handoff implies the outgoing track played ~fully: a real advance
  // resets the instant-death run (a convergence — server kept our file —
  // leaves it alone).
  if ((state.currentSong?.file ?? null) !== (outFile ?? null)) instantDeaths = 0
  const song = state.currentSong
  return song ? toTrack(song) : null
}

engine.setNextProvider(
  nextForFade,
  async (_expected: EngineTrack, outFile: string | null) => advanceForFade(outFile),
)

// Recovery URLs refresh expired access tokens (stream requests bypass the
// API client's 401 path); buffering drives the player-bar indicator.
engine.setStreamUrlProvider((file, isPodcast) => api.freshStreamUrl(file, isPodcast))
engine.onBuffering((buffering) => usePlayer.setState({ buffering }))
engine.onFatal((file) => {
  if (hasPendingPlaybackIntent()) return
  const st = usePlayer.getState()
  if (!st.isPlaying || st.currentSong?.file !== file) return
  // Deterministic media failure (decode/404): the existing instant-death
  // breaker decides how many broken files to skip before parking.
  autoAdvance(true)
})

// Engine diagnostics → server logs (throttled inside the engine): seek
// targets, spurious ends, element errors, stall recoveries. This is what
// makes far-seek stalls on huge files diagnosable after the fact.
engine.onReport = (message) => {
  try {
    if (typeof localStorage === 'undefined' || !localStorage.getItem('hathor:token')) return
    void api.logClient(`audio engine: ${message}`.slice(0, 2000), location.pathname).catch(() => {})
  } catch {
    /* reporting must never break playback */
  }
}

engine.onEnded(() => {
  // Completion telemetry is exact here, before the advance response swaps
  // the engine to the next track.
  recordEndedPlayback(engine.time(), engine.duration())
  // Classify while the element still holds the outgoing track (the advance
  // response hasn't applied yet): played to a sane end, or real media time
  // observed, means a genuine finish and resets the chain.
  let diedInstantly = true
  try {
    const d = engine.duration()
    diedInstantly = !(d > 0 && engine.time() >= d - 2) && engine.lastGoodPosition() < INSTANT_DEATH_MEDIA_SEC
  } catch {
    diedInstantly = true
  }
  autoAdvance(diedInstantly)
})

// Consecutive instant-death breaker: corrupt/empty files end within moments
// of loading and the legacy rule advances past each one — a bad batch (or a
// replay loop) then auto-skips several tracks in seconds. Genuine finishes
// (played to the end, or real media time observed) reset the run, and only
// automatic advances evaluate it, so a user hopping short clips manually
// can never trip it. On break the chain pauses instead of sailing on.
const INSTANT_DEATH_MEDIA_SEC = 8
const INSTANT_DEATH_BREAK_AFTER = 3
let instantDeaths = 0
// Test-only reset (the streak is module state like transportGen).
export function resetAutoAdvanceStreak(): void {
  instantDeaths = 0
}
// True when the chain must break (caller pauses instead of advancing).
export function noteAutoAdvance(diedInstantly: boolean): boolean {
  if (diedInstantly) instantDeaths += 1
  else instantDeaths = 0
  return instantDeaths >= INSTANT_DEATH_BREAK_AFTER
}

function breakAdvanceChain(): void {
  instantDeaths = 0
  const message = 'auto-advance chain broken: repeated instant track deaths (corrupt batch?)'
  try {
    if (typeof console !== 'undefined' && console.warn) console.warn(`[audio] ${message}`)
  } catch {
    /* reporting must never break playback */
  }
  try {
    if (typeof localStorage !== 'undefined' && localStorage.getItem('hathor:token'))
      void api.logClient(`audio engine: ${message}`.slice(0, 2000), location.pathname).catch(() => {})
  } catch {
    /* reporting must never break playback */
  }
  void api
    .pause()
    .then((s) => {
      usePlayer.setState(s)
      usePlayer.getState().syncAudio()
    })
    .catch(() => {})
}

// Raw queue advance with one guarded retry: a lost auto-advance strands the
// finished song at the edge forever (pause/unpause just re-parks it). Chain
// counting happens in the callers via noteAutoAdvance, not here.
function requestNext(auto: boolean): void {
  const expected = usePlayer.getState().currentSong?.file ?? null
  void api
    .next(expected, auto)
    .then((s) => {
      usePlayer.setState(s)
      syncEngine(s)
    })
    .catch(() => {
      globalThis.setTimeout?.(() => {
        try {
          // A manual track change in between wins; the stale retry dissolves
          // instead of clobbering it.
          const cur = usePlayer.getState()
          if (cur.currentSong?.file !== expected || engine.currentFile() !== expected) return
          void api
            .next(expected, auto)
            .then((s2) => {
              if (usePlayer.getState().currentSong?.file !== expected) return
              usePlayer.setState(s2)
              syncEngine(s2)
            })
            .catch(() => {})
        } catch {
          /* a dead retry must never break playback */
        }
      }, 2000)
    })
}

// Automatic advance from a finished track. diedInstantly marks runs of
// files that die moments after loading (corrupt batch / replay loop).
// Exported for tests (the onEnded/onTailStuck wiring itself is module scope).
export function autoAdvance(diedInstantly: boolean): void {
  if (noteAutoAdvance(diedInstantly)) {
    breakAdvanceChain()
    return
  }
  requestNext(!diedInstantly)
}

// Tail strand: the final bytes are unplayable so `ended` never fires (the
// bar freezes ~a second short and toggles just re-park it). Repeat-one
// replays the track; otherwise this counts as an instant death and advances
// like a natural finish. A deliberately paused strand stays put.
engine.onTailStuck(() => {
  const st = usePlayer.getState()
  if (!st.isPlaying) return
  if (st.repeat) {
    engine.seek(0)
    void api
      .seek(0)
      .then((s) => {
        usePlayer.setState(s)
        usePlayer.getState().syncAudio()
      })
      .catch(() => {})
    return
  }
  autoAdvance(true)
})

export const usePlayer = create<PlayerStore>((set, get) => ({
  currentSong: null,
  isPlaying: false,
  positionSec: 0,
  volume: 0.7,
  shuffle: false,
  repeat: false,
  queue: [],
  source: null,
  isCustomQueue: false,
  firstPlay: true,
  queueTotal: 0,
  queueVersion: 0,
  normalize: loadNormalize(),
  chapterSkip: loadChapterSkip(),
  buffering: false,
  syncing: false,

  setNormalize: (enabled: boolean) => {
    try {
      localStorage.setItem(NORMALIZE_KEY, enabled ? '1' : '0')
    } catch {
      // private-mode storage may reject writes — engine flag still applies
    }
    set({ normalize: enabled })
    engine.setNormalize(enabled)
  },

  setChapterSkip: (enabled: boolean) => {
    try {
      localStorage.setItem(CHAPTER_SKIP_KEY, enabled ? '1' : '0')
    } catch {
      // private-mode storage may reject writes — the flag still applies
    }
    set({ chapterSkip: enabled })
    // Persist account-wide so any timestamped podcast auto-skips on every
    // device; the local flag already applies instantly if this fails.
    void api.updateSettings({ chapterSkip: enabled }).catch(() => {})
  },

  refresh: async () => {
    const state = await api.playerState()
    if (playbackIntent) {
      // A pause/resume command is still retrying: the server may not have
      // applied it yet. Pull metadata/queue but never let a stale
      // isPlaying/position resurrect audio against the user's intent.
      set({ ...state, isPlaying: playbackIntent.playing })
      if (!playbackIntent.playing) engine.pause()
      return
    }
    set(state)
    syncEngine(state)
  },

  // Startup: pull state, then rebuild an empty queue from the persisted
  // queue_source (desktop load_current_song → _rebuild_queue_from_source).
  // A refresh never resumes audio: if the server still shows playing (the
  // tab died mid-song), pause it first so UI, engine and API agree.
  boot: async () => {
    const pulled = await api.playerState()
    let state = pulled
    if (pulled.isPlaying && pulled.currentSong) {
      // The tab died mid-song (or another device left it playing): pause
      // immediately in the UI, then use the reliable intent so a flaky API
      // cannot leave the server running into the next track.
      state = { ...pulled, isPlaying: false }
      set(state)
      const intent = startPlaybackIntent(false, pulled.currentSong.file, null)
      await attemptPlaybackIntent(intent)
      state = get()
    } else {
      set(state)
    }
    if (state.queue.length === 0 && state.source && state.currentSong && !state.firstPlay) {
      try {
        const { state: rebuilt } = await api.queueRebuild()
        set(rebuilt)
      } catch {
        // stale source — general list fallback stays as-is
      }
    }
    try {
      const settings = await api.settings()
      engine.setCrossfade(settings.crossfadeEnabled, settings.crossfadeSeconds)
      // Server wins at startup: the account setting drives auto-skip on
      // every device; the browser copy is only a pre-login fallback.
      set({ chapterSkip: settings.chapterSkip ?? get().chapterSkip })
      try {
        localStorage.setItem(CHAPTER_SKIP_KEY, get().chapterSkip ? '1' : '0')
      } catch {
        // ignore private-mode write failures
      }
    } catch {
      // settings fetch must never break boot; engine stays faded off
    }
    engine.setVolume(get().volume)
    engine.setNormalize(get().normalize)
    syncEngine(get())
  },

  playSong: async (song, context, source) => {
    // populate_queue_from_list equivalent, then play (two headless calls).
    // isPodcast keeps episodes out of music history (desktop rule).
    // Start local audio immediately (user gesture unlocks the AudioContext)
    // so the browser buffers during the round trips instead of after them.
    // If either call fails, the store still names the previous track while
    // the element already plays the new one — reconverge on the server
    // (refresh re-syncs the element too) instead of leaving the pair
    // diverged, then rethrow so callers see the failure as before.
    cancelPlaybackIntent()
    engine.ensureContext()
    const restart = get().currentSong?.file === song.file || engine.currentFile() === song.file
    engine.load(toTrack(song), true, restart)
    // Optimistic: the UI (player bar, row highlight, lyrics) names the
    // clicked song instantly instead of flashing the old one through both
    // server round trips. The responses below confirm or correct it; a
    // failure reconverges via refresh in the catch.
    set({ currentSong: song, isPlaying: true })
    const gen = claimTransport()
    try {
      // The queue response is awaited for server ordering but NEVER applied:
      // queue-replace doesn't change the current track, so its state still
      // names the OLD song — applying it flips the UI back mid-flight and
      // is the back-and-forth on every song click (next-track clicks have
      // no intermediate response, which is why they never flickered). The
      // play response below delivers the full confirmed state instead.
      await api.setQueue(song.file, context, source)
      // A newer click already moved on: anything below names a stale track
      // and must never overwrite the store or reload the engine (that is
      // what kept resurrecting old songs between every new one).
      if (!isCurrentTransport(gen)) return
      const state = await api.play(song.file, song.isPodcast, newInstanceId())
      if (!isCurrentTransport(gen)) return
      set(state)
      syncEngine(state)
    } catch (e) {
      if (!isCurrentTransport(gen)) return
      await get()
        .refresh()
        .catch(() => {})
      throw e
    } finally {
      releaseTransport()
    }
  },

  playSongByFiles: async (song, files, source) => {
    // Same contract as playSong, but the queue context arrives as ordered
    // file names (infinite scroll never holds the full Song[] list).
    // The clicked song object still drives the optimistic UI + engine load.
    cancelPlaybackIntent()
    engine.ensureContext()
    const restart = get().currentSong?.file === song.file || engine.currentFile() === song.file
    engine.load(toTrack(song), true, restart)
    set({ currentSong: song, isPlaying: true })
    const gen = claimTransport()
    try {
      await api.setQueueFiles(song.file, files, source)
      if (!isCurrentTransport(gen)) return
      const state = await api.play(song.file, song.isPodcast, newInstanceId())
      if (!isCurrentTransport(gen)) return
      set(state)
      syncEngine(state)
    } catch (e) {
      if (!isCurrentTransport(gen)) return
      await get()
        .refresh()
        .catch(() => {})
      throw e
    } finally {
      releaseTransport()
    }
  },

  skipNext: async () => {
    cancelPlaybackIntent()
    const gen = claimTransport()
    try {
      const state = await api.next()
      if (!isCurrentTransport(gen)) return
      set(state)
      syncEngine(state)
    } catch {
      if (isCurrentTransport(gen)) await get().refresh().catch(() => {})
    } finally {
      releaseTransport()
    }
  },

  skipPrev: async () => {
    cancelPlaybackIntent()
    const gen = claimTransport()
    try {
      const state = await api.prev()
      if (!isCurrentTransport(gen)) return
      set(state)
      syncEngine(state)
    } catch {
      if (isCurrentTransport(gen)) await get().refresh().catch(() => {})
    } finally {
      releaseTransport()
    }
  },

  toggleRepeat: async () => {
    const gen = ++repeatGen
    try {
      const state = await api.repeat()
      if (gen !== repeatGen) return
      set(state)
    } catch {
      /* playerHub/refresh will converge on the next broadcast */
    }
  },

  toggle: async () => {
    // Optimistic flip: pause/play locally first (instant), then send an
    // explicit idempotent command. A failed round trip keeps the local
    // state and retries, so the backend cannot keep advancing the track
    // and change songs when the user finally unpauses. During a fade the
    // element's active slot is still the outgoing track while the store
    // names the incoming one, so pausing there must not seek the incoming
    // file to the outgoing position.
    const before = get()
    const desired = !before.isPlaying
    const fading = engine.isFading()
    const positionSec = desired || fading ? null : engine.time()
    const canFlip =
      before.currentSong !== null &&
      (fading || engine.currentFile() === before.currentSong.file)
    // Pausing is always safe locally; resuming only when the element is
    // already on the track the store names (otherwise the response loads it).
    if (!desired) engine.pause()
    else if (canFlip) engine.play()
    set({ isPlaying: desired })
    const intent = startPlaybackIntent(desired, before.currentSong?.file ?? null, positionSec)
    await attemptPlaybackIntent(intent)
  },

  // Re-point the engine at whatever the server says is current
  // (used after remote-style mutations that don't go through playSong).
  syncAudio: () => {
    syncEngine(get())
  },
}))

export function formatTime(sec: number) {
  const s = Math.max(0, Math.floor(sec || 0))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

// Started by Shell so tests (and logged-out screens) never emit telemetry.
export function startPlayerTelemetry(): () => void {
  return startPlaybackTelemetry(
    () => usePlayer.getState(),
    () => ({ position: engine.time(), duration: engine.duration() }),
  )
}
