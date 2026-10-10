import { create } from 'zustand'
import { api, type PlayerState, type QueueSource, type Song } from '../api/client'
import { engine, type EngineTrack } from '../audio/engine'

// Client playback store. Server PlaybackState is the source of truth
// (pulled on load); the AudioEngine renders it with gapless handoff and
// equal-power crossfade. Fades never move server state except at the fade
// start, when the engine asks the store to advance (api.next) exactly as
// the new audio starts — server position and new track stay aligned.
interface PlayerStore extends PlayerState {
  refresh: () => Promise<void>
  boot: () => Promise<void>
  playSong: (song: Song, context: Song[], source: QueueSource | null) => Promise<void>
  toggle: () => Promise<void>
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
  // The correction is capped at the track's known length: a wall-clock
  // estimate that outran a stalled element must never yank playback onto
  // the duration edge (which fires `ended` and skips mid-song). engine.seek
  // clamps to the element duration when metadata is loaded; the catalog
  // duration covers the metadata-pending window.
  const pos = state.positionSec
  if (!engine.isFading() && Number.isFinite(pos) && pos >= 0 && Math.abs(engine.time() - pos) > 2) {
    const elDur = engine.duration()
    const known = elDur > 0 ? elDur : (song?.duration ?? 0)
    engine.seek(known > 0 ? Math.min(pos, known) : pos)
  }
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
  const song = state.currentSong
  return song ? toTrack(song) : null
}

engine.setNextProvider(
  () => {
    const next = usePlayer.getState().queue[0]
    return next ? toTrack(next) : null
  },
  async (_expected: EngineTrack, outFile: string | null) => advanceForFade(outFile),
)

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
  void api
    .next()
    .then((s) => {
      usePlayer.setState(s)
      syncEngine(s)
    })
    .catch(() => {})
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
  normalize: loadNormalize(),
  chapterSkip: loadChapterSkip(),

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
      try {
        state = await api.pause()
      } catch {
        state = { ...pulled, isPlaying: false }
      }
    }
    set(state)
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
    engine.ensureContext()
    engine.load(toTrack(song), true)
    try {
      const queued = await api.setQueue(song.file, context, source)
      set(queued)
      const state = await api.play(song.file, song.isPodcast)
      set(state)
      syncEngine(state)
    } catch (e) {
      await get()
        .refresh()
        .catch(() => {})
      throw e
    }
  },

  toggle: async () => {
    // Optimistic flip: pause/play locally first (instant), then let the
    // server confirm — it stays source of truth for remote control and
    // reconciles us on response. Only when a song is loaded; otherwise
    // the server decides (nothing loaded yet).
    const before = get()
    // During a fade the element's active slot is still the outgoing track
    // while the store already names the incoming one — by design, not
    // divergence — so a fading engine always allows the instant flip.
    // Without this, pausing mid-fade does nothing locally: audio plays on
    // until the server round trip, and a fade handoff landing after the
    // pause resurrects playback entirely.
    const canFlip =
      before.currentSong !== null &&
      (engine.isFading() || engine.currentFile() === before.currentSong.file)
    if (canFlip) {
      if (before.isPlaying) engine.pause()
      else engine.play()
      set({ isPlaying: !before.isPlaying })
    }
    try {
      const state = await api.toggle()
      set(state)
      syncEngine(state)
    } catch {
      // Server unreachable — resync to whatever it actually has.
      void before.refresh().catch(() => {})
    }
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
