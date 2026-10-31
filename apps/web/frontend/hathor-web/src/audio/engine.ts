// Double-buffered playback engine with equal-power crossfade.
//
// Two HTMLAudioElements take turns as the audible ("active") track. Each is
// permanently wired element → GainNode → master → destination, so a track
// change can overlap the tail of the old track with the head of the new one
// (like Spotify's crossfade) instead of hard-cutting a single element.
// Gains follow the equal-power curve (cos/sin), which preserves perceived
// loudness — a linear ramp would dip audibly in the middle.
//
// Optional volume normalization inserts a DynamicsCompressor between master
// and destination (all tracks incl. podcasts). It is leveling, not measured
// loudness matching (see Stage 2 loudnorm plan): it evens out loud/quiet
// material with zero analysis. Purely client-side, persisted in localStorage.
//
// Design notes:
// - The graph runs on the audio thread, but fades are *driven* by a plain
//   interval timer reading media time, so background tabs (throttled timers),
//   pause/resume and seeks stay consistent — nothing depends on wall-clock.
// - The server stays the source of truth: at fade start the engine asks the
//   store to advance (api.next), exactly when the new audio starts, so the
//   server position estimate and the new track stay aligned.
// - Podcasts never fade (Spotify parity): hard switch only. Normalization,
//   by explicit choice, DOES apply to podcasts.
// - No AudioContext (ancient browsers, some tests) → bypass mode: plain
//   single-track behavior, no fades, no normalization, everything else
//   identical.

export interface EngineTrack {
  file: string
  url: string
  isPodcast: boolean
  // Measured-loudness correction in dB (null = unmeasured, gain 1).
  // Applied per slot so fades multiply, never fight, the correction.
  gainDb: number | null
}

// Streaming-style target (mirrors backend LoudnessGain.TargetLufs /
// MaxCorrectionDb — keep the constants in sync).
export const LOUDNESS_TARGET_LUFS = -14
export const LOUDNESS_MAX_CORRECTION_DB = 12

export function gainForDb(gainDb: number | null | undefined): number {
  if (gainDb === null || gainDb === undefined || !Number.isFinite(gainDb)) return 1
  const clamped = Math.min(LOUDNESS_MAX_CORRECTION_DB, Math.max(-LOUDNESS_MAX_CORRECTION_DB, gainDb))
  return Math.pow(10, clamped / 20)
}

export interface EngineDeps {
  createElement: () => AudioElementLike
  createContext: () => AudioContextLike
  now: () => number
}

// Structural minimums so tests can inject fakes (jsdom has no AudioContext).
export interface AudioElementLike {
  src: string
  currentTime: number
  duration: number
  paused: boolean
  seeking: boolean
  readyState: number
  // NETWORK_EMPTY=0 IDLE=1 LOADING=2 NO_SOURCE=3. NO_SOURCE/EMPTY with a
  // loaded file means the stream unloaded underneath us (the long-file
  // seek stall) — the watchdog treats that as a stall even when paused.
  networkState: number
  error: { readonly code: number; readonly message: string } | null
  volume: number
  play(): Promise<void> | void
  pause(): void
  load(): void
  addEventListener(type: 'ended' | 'error', fn: () => void): void
}

export interface GainParamLike {
  value: number
  setTargetAtTime?(target: number, startTime: number, timeConstant: number): void
}

export interface GainNodeLike {
  gain: GainParamLike
  connect(node: unknown): void
  disconnect(): void
}

export interface DynamicsCompressorLike {
  threshold: GainParamLike
  knee: GainParamLike
  ratio: GainParamLike
  attack: GainParamLike
  release: GainParamLike
  connect(node: unknown): void
  disconnect(): void
}

export interface AudioSourceLike {
  connect(node: unknown): void
}

export interface AnalyserNodeLike {
  fftSize: number
  smoothingTimeConstant: number
  frequencyBinCount: number
  getByteFrequencyData(array: Uint8Array): void
  connect(node: unknown): void
}

export interface AudioContextLike {
  readonly destination: unknown
  readonly state: string
  readonly currentTime?: number
  readonly sampleRate?: number
  readonly baseLatency?: number
  readonly outputLatency?: number
  createGain(): GainNodeLike
  createDynamicsCompressor(): DynamicsCompressorLike
  createMediaElementSource(el: AudioElementLike): AudioSourceLike
  createAnalyser?(): AnalyserNodeLike
  resume(): Promise<void> | void
}

interface Slot {
  el: AudioElementLike
  gain: GainNodeLike | null
  file: string | null
  url: string | null
  isPodcast: boolean
  baseGain: number
  recoveries: number
}

interface Fade {
  out: Slot
  incoming: Slot
  start: number
  dur: number
  pauseBeganAt: number | null
}

const TICK_MS = 200
const PRELOAD_AHEAD_SEC = 10
const MIN_FADE_SEC = 1
const MAX_FADE_SEC = 12
// Stay this far from the duration edge: landing exactly on it fires
// `ended` and the track advances away (the long-podcast seek stall).
export const END_EPSILON_SEC = 0.25
// Drift corrections stop this far short of the known length (client +
// catalog): server estimates can outrun a stalled element, so correcting
// exactly onto the edge would fire `ended` and skip mid-track. The tail
// still plays out naturally on resume.
export const DRIFT_EDGE_MARGIN_SEC = 2
// An `ended` this soon after a seek whose target was safely inside the
// track is a spurious end (bad range past EOF), not a real finish.
const SPURIOUS_END_WINDOW_MS = 1500
// An `ended` this far short of a sane known duration is a cut-short stream
// (truncated file), not a real finish — recover instead of advancing.
// Durations at/below the minimum keep the legacy advance behavior so
// corrupt/empty files still skip past instead of pausing the queue.
const CUT_SHORT_GAP_SEC = 3
const CUT_SHORT_MIN_DURATION_SEC = 5
// A playing element whose clock freezes this long gets one recovery seek;
// then we give up (pause) instead of fake-playing forever.
const STALL_TIMEOUT_MS = 10000
const MAX_RECOVERIES = 3
// Grace after (re)load and seeks: EMPTY is the normal transient while the
// element fetches — only an old, settled emptiness means unload.
const UNLOAD_GRACE_MS = 5000
// A strand this close to a known end counts as "finished" rather than
// "broken mid-track": the final bytes are unplayable, `ended` will never
// fire, and pausing there freezes the bar ~a second short forever (every
// pause/unpause just re-parks the same spot, never advancing). Kept small
// so a flaky network can never skip real content — mid-track failures keep
// the legacy pause behavior.
const TAIL_STUCK_WINDOW_SEC = 5
// Minimum gap between tail-advance attempts: a failed advance (lost
// response) must not double-advance the queue on every toggle.
const TAIL_STUCK_COOLDOWN_MS = 30000
// Network-class failures retry forever while playback is intended: a flaky
// connection must never silently strand a song with the UI still saying
// "playing". Backoff caps so a long outage keeps trying without a hot loop.
const RETRY_BASE_MS = 500
const RETRY_MAX_MS = 30000
// Healthy playback for this long resets the backoff to the fast path.
const HEALTHY_RESET_MS = 15000
// Deterministic media failures (decode / unsupported source) get a few
// fresh-URL attempts, then the store advances past the broken file.
const FATAL_MEDIA_RETRIES = 3
// Ignore transient readyState dips shorter than this before showing the
// buffering state (normal loads/seek refills would otherwise flash).
const BUFFERING_AFTER_MS = 700

// Clamp a seek target into playable range. Duration-unknown (NaN, metadata
// not loaded yet) keeps the old behavior — nothing better is knowable.
export function clampSeekTarget(sec: number, duration: number): number {
  if (!Number.isFinite(sec)) return 0
  if (!Number.isFinite(duration) || duration <= 0) return Math.max(0, sec)
  return Math.min(Math.max(0, sec), Math.max(0, duration - END_EPSILON_SEC))
}

// Equal-power gains at progress t ∈ [0, 1]: [outgoing, incoming].
// Midpoint is -3 dB each — constant total power, no middle dip.
export function fadeGains(t: number): [number, number] {
  const c = Math.min(1, Math.max(0, t))
  return [Math.cos((c * Math.PI) / 2), Math.sin((c * Math.PI) / 2)]
}

export class AudioEngine {
  private readonly deps: EngineDeps
  private ctx: AudioContextLike | null = null
  private master: GainNodeLike | null = null
  private normalizer: DynamicsCompressorLike | null = null
  private normalizeEnabled = false
  private graphOk = true
  private contextReported = false
  private a: Slot
  private b: Slot
  private active: Slot
  private musicBus: GainNodeLike | null = null
  private analyser: AnalyserNodeLike | null = null
  private voice: AudioElementLike
  private voiceQueue: string[] = []
  private voiceDuckLevel = 0.18
  private voiceActive = false
  private voiceDoneCb: (() => void) | null = null
  private voiceEndedCb: () => void = () => {}
  private djLeadSec = 0
  private djCueCb: ((file: string, remaining: number) => void) | null = null
  private lastDjCueFile: string | null = null
  private fading: Fade | null = null
  private volume = 0.7
  private fadeEnabled = false
  private fadeSec = 5
  private peekNext: () => EngineTrack | null = () => null
  private beginNext: (expected: EngineTrack, outFile: string | null) => Promise<EngineTrack | null> =
    async () => null
  private endedCb: () => void = () => {}
  private tailStuckCb: () => void = () => {}
  private fatalCb: (file: string, reason: string) => void = () => {}
  private bufferingCb: (buffering: boolean) => void = () => {}
  private timer: ReturnType<typeof setInterval> | null = null
  // Explicit playback intent. The element can sit paused because of a
  // failed load/stall; that is not a user pause and must keep retrying.
  private wantPlaying = false
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private retryAttempt = 0
  private retrying = false
  private retryEpoch = 0
  private lastRetryAt = -Infinity
  private buffering = false
  private streamUrl: ((file: string, isPodcast: boolean) => string | Promise<string>) | null = null
  // Debug sink: the store wires this to the server log bridge (throttled).
  // Seeks/ends/errors/recoveries report here — the element swallows them
  // otherwise, which is exactly how far-seek stalls went undiagnosed.
  // Null = console only.
  onReport: ((message: string) => void) | null = null
  private lastReportAt = -Infinity
  // Throttled error reporting (server bridge + console). Seeks log at
  // debug level only — they fire constantly during scrubs.
  private report(message: string, debugOnly = false) {
    try {
      if (!debugOnly && typeof console !== 'undefined' && console.warn) {
        console.warn(`[audio] ${message}`)
      } else if (typeof console !== 'undefined' && console.debug) {
        console.debug(`[audio] ${message}`)
      }
      if (!debugOnly && this.onReport && this.deps.now() - this.lastReportAt > 10000) {
        this.lastReportAt = this.deps.now()
        this.onReport(message)
      }
    } catch {
      /* reporting must never break playback */
    }
  }

  // One-time lifecycle diagnostics (graph creation): bypass the 10s report
  // throttle so the first playback still forwards later warnings.
  private reportOnce(message: string) {
    try {
      if (typeof console !== 'undefined' && console.warn) console.warn(`[audio] ${message}`)
      this.onReport?.(message)
    } catch {
      /* reporting must never break playback */
    }
  }

  private describeSlot(slot: Slot): string {
    let ready = '?'
    let err = 'none'
    let net = '?'
    try {
      ready = String(slot.el.readyState ?? '?')
      err = slot.el.error ? `${slot.el.error.code}:${slot.el.error.message}` : 'none'
      net = String(slot.el.networkState ?? '?')
    } catch {
      /* describe best-effort only */
    }
    return `file=${slot.file} pos=${slot.el.currentTime} dur=${slot.el.duration} ` +
      `paused=${slot.el.paused} seeking=${slot.el.seeking} readyState=${ready} net=${net} error=${err}`
  }

  private setBuffering(next: boolean) {
    if (this.buffering === next) return
    this.buffering = next
    try {
      this.bufferingCb(next)
    } catch {
      /* UI reporting must never break playback */
    }
  }

  private mediaErrorCode(slot: Slot): number | null {
    try {
      return slot.el.error ? slot.el.error.code : null
    } catch {
      return null
    }
  }

  // 1 = aborted (often a src swap/seek), 2 = network. No error object means
  // the unload/watchdog path — both are transient and worth retrying.
  private isTransientMediaError(slot: Slot): boolean {
    const code = this.mediaErrorCode(slot)
    return code === null || code === 1 || code === 2
  }

  private clearRetryTimer() {
    if (this.retryTimer !== null) {
      clearTimeout(this.retryTimer)
      this.retryTimer = null
    }
  }

  private pauseQuietly(el: AudioElementLike) {
    try {
      el.pause()
    } catch {
      /* a dead element may throw */
    }
  }

  // Cancel pending retries and invalidate any in-flight URL resolution.
  // resetAttempts=false keeps the backoff history (used when playback
  // recovers on its own or the user seeks); pause/load fully reset it.
  private cancelRetry(resetAttempts = true) {
    this.clearRetryTimer()
    this.retrying = false
    this.retryEpoch += 1
    if (resetAttempts) {
      this.retryAttempt = 0
      this.lastRetryAt = -Infinity
    }
    this.setBuffering(false)
  }

  // Network/unload/stall retries are unbounded while playback is intended.
  // A hard 3-strike cap is exactly what stranded songs with a lying UI.
  private scheduleRetry(slot: Slot, reason: string) {
    if (slot !== this.active || !this.wantPlaying || !slot.file) {
      this.setBuffering(false)
      return
    }
    if (this.retryTimer !== null) return // already scheduled
    // Stranded at the tail after several retries: the remaining bytes are
    // unplayable and `ended` will never fire. Advance instead of retrying
    // forever a second short of the end (mid-track failures keep retrying).
    if (this.strandIsTail() && this.retryAttempt >= MAX_RECOVERIES) {
      if (this.deps.now() - this.lastTailAdvanceAt > TAIL_STUCK_COOLDOWN_MS) {
        this.handleExhausted(slot, 'error')
      } else {
        // An advance already fired inside the cooldown and hasn't landed:
        // park instead of fake-retrying or double-advancing the queue.
        this.wantPlaying = false
        this.pauseQuietly(slot.el)
        this.setBuffering(false)
        this.retrying = false
      }
      return
    }
    const delay = Math.min(RETRY_MAX_MS, RETRY_BASE_MS * 2 ** Math.min(this.retryAttempt, 6))
    this.retryAttempt += 1
    this.retrying = true
    this.lastRetryAt = this.deps.now()
    this.retryEpoch += 1
    const epoch = this.retryEpoch
    this.setBuffering(true)
    this.report(`retry #${this.retryAttempt} in ${delay}ms (${reason}) ${this.describeSlot(slot)}`)
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null
      if (epoch !== this.retryEpoch) return
      void this.runRetry(slot, reason)
    }, delay)
  }

  private async runRetry(slot: Slot, reason: string) {
    const epoch = this.retryEpoch
    if (slot !== this.active || !this.wantPlaying || !slot.file) {
      this.retrying = false
      this.setBuffering(false)
      return
    }
    let url = slot.url ?? ''
    try {
      if (this.streamUrl) {
        const fresh = await this.streamUrl(slot.file, slot.isPodcast)
        if (epoch !== this.retryEpoch) return
        if (fresh) url = fresh
      }
    } catch (e: unknown) {
      this.report(`stream URL refresh failed: ${e instanceof Error ? e.message : String(e)}`)
    }
    if (epoch !== this.retryEpoch || slot !== this.active || !this.wantPlaying) return
    try {
      if (url) {
        slot.url = url
        slot.el.src = url
        // Re-assigning the same URL does not always restart the pipeline;
        // load() forces a fresh fetch after a failed range/expired token.
        slot.el.load()
      }
      // Treat the attempt as a fresh load so the paused-watchdog cannot
      // schedule a second retry before play() settles. lastLoadAt itself
      // stays the file's original load time — driftTarget uses
      // (lastRecoveryAt > lastLoadAt) to engage its recovery ceiling.
      this.lastRetryRanAt = this.deps.now()
      this.lastRecoveryAt = this.deps.now()
      this.lastSeekAt = this.deps.now()
      this.lastSeekTarget = Math.max(0, this.lastGoodTime)
      this.lastAdvanceAt = this.deps.now()
      this.retrying = false
      try {
        slot.el.currentTime = Math.max(0, this.lastGoodTime)
      } catch (e: unknown) {
        this.report(`retry seek threw: ${e instanceof Error ? e.message : String(e)}`)
      }
      this.tryPlay(slot.el, `retry-${reason}`)
    } catch (e: unknown) {
      this.report(`retry threw: ${e instanceof Error ? e.message : String(e)}`)
      this.scheduleRetry(slot, `${reason}-threw`)
    }
  }

  // Called when the browser comes back online: retry immediately instead of
  // waiting out a 30s backoff.
  retryNow() {
    if (!this.wantPlaying || !this.active.file) return
    try {
      if (!this.active.el.paused && this.active.el.readyState >= 3 && !this.isUnloaded(this.active)) return
    } catch {
      /* retry below regardless */
    }
    this.clearRetryTimer()
    this.retryAttempt = 0
    this.retrying = true
    this.lastRetryAt = this.deps.now()
    this.retryEpoch += 1
    this.setBuffering(true)
    void this.runRetry(this.active, 'online')
  }

  private handleFatalMedia(slot: Slot, reason: string) {
    if (slot !== this.active) return
    const file = slot.file
    this.cancelRetry(true)
    this.wantPlaying = false
    this.report(`fatal media failure (${reason}), handing off ${this.describeSlot(slot)}`)
    try {
      slot.el.pause()
    } catch {
      /* ignore */
    }
    if (file) {
      try {
        this.fatalCb(file, reason)
      } catch {
        /* the store's recovery must never break the engine */
      }
    }
  }

  private tryPlay(el: AudioElementLike, why: string, retry = true) {
    try {
      const p = el.play() as Promise<void> | undefined
      if (p && typeof p.catch === 'function') {
        p.catch((e: unknown) => {
          const name = e instanceof Error ? e.name : ''
          // Suspended AudioContext after minutes backgrounded: the resume()
          // in ensureContext is async, so the first play() can lose the race
          // (NotAllowedError) and leave UI-playing/element-paused with a
          // frozen bar. One delayed retry — skipped if the user paused in
          // the meantime, so a pause can never resurrect audio.
          if (retry && (name === 'NotAllowedError' || name === 'AbortError')) {
            try {
              void this.ctx?.resume()
            } catch {
              /* ignore */
            }
            globalThis.setTimeout?.(() => {
              try {
                if (el.paused) this.tryPlay(el, `${why}-retry`, false)
              } catch {
                /* retry must never throw */
              }
            }, 300)
          }
          this.report(`play() rejected (${why}): ${e instanceof Error ? e.message : String(e)}`)
        })
      }
    } catch (e: unknown) {
      this.report(`play() threw (${why}): ${e instanceof Error ? e.message : String(e)}`)
    }
  }
  // Last position known to actually play (recovery target) + last seek
  // (spurious-end guard). Wall clock via deps.now() for testability.
  private lastGoodTime = 0
  // Last recovery moment: after any recovery ran for the loaded file, drift
  // corrections must not yank playback forward past lastGoodTime (the
  // observed-playable high-water mark) — that re-enters the region that
  // already stalled and loops until the budget is exhausted (the
  // slider-seek freeze: frozen bar, dead toggle). Reset on file change.
  // A fresh user seek after the failure lifts the ceiling (explicit intent
  // retries the spot); system seeks never do.
  private lastRecoveryAt = -Infinity
  private lastUserSeekAt = -Infinity
  // Last tail-advance fire: cooldown-guarded so a failed advance (lost
  // response) cannot double-advance the queue on every toggle.
  private lastTailAdvanceAt = -Infinity
  private lastAdvanceAt = 0
  private lastSeekAt = -Infinity
  private lastSeekTarget = 0
  private lastLoadAt = -Infinity
  private lastRetryRanAt = -Infinity

  constructor(deps?: Partial<EngineDeps>) {
    const createElement =
      deps?.createElement ??
      (() => {
        const el = new Audio()
        el.preload = 'auto'
        el.volume = 1
        return el as unknown as AudioElementLike
      })
    const contextFactory =
      deps?.createContext ??
      ((): AudioContextLike => {
        // Music playback does not need the tiny interactive buffer: a
        // larger render quantum makes Android/Bluetooth headphone routing
        // far less likely to underrun. Pinning the context to the library's
        // 48 kHz source rate also skips one media-element resampling stage.
        try {
          return new AudioContext({
            latencyHint: 'playback',
            sampleRate: 48000,
          }) as unknown as AudioContextLike
        } catch {
          try {
            return new AudioContext({ latencyHint: 'playback' }) as unknown as AudioContextLike
          } catch {
            return new AudioContext() as unknown as AudioContextLike
          }
        }
      })
    this.deps = {
      createElement,
      createContext: contextFactory,
      now: deps?.now ?? (() => performance.now()),
    }
    const mkSlot = (): Slot => ({ el: this.deps.createElement(), gain: null, file: null, url: null, isPodcast: false, baseGain: 1, recoveries: 0 })
    this.a = mkSlot()
    this.b = mkSlot()
    this.voice = this.deps.createElement()
    this.active = this.a
    this.a.el.addEventListener('ended', () => this.handleEnded(this.a))
    this.b.el.addEventListener('ended', () => this.handleEnded(this.b))
    this.a.el.addEventListener('error', () => this.handleSlotError(this.a))
    this.b.el.addEventListener('error', () => this.handleSlotError(this.b))
    this.voice.addEventListener('ended', () => this.handleVoiceEnded())
    this.voice.addEventListener('error', () => this.handleVoiceEnded())
  }

  // ---- configuration -------------------------------------------------

  setVolume(v: number) {
    if (!Number.isFinite(v)) return
    this.volume = v
    if (this.master) this.master.gain.value = v
    else {
      // Bypass mode: scale the elements directly.
      const music = v * (this.voiceActive ? this.voiceDuckLevel : 1)
      this.a.el.volume = music
      this.b.el.volume = music
      this.voice.volume = this.voiceActive ? v : 0
    }
  }

  setCrossfade(enabled: boolean, seconds: number) {
    this.fadeEnabled = enabled
    this.fadeSec = Math.min(MAX_FADE_SEC, Math.max(MIN_FADE_SEC, seconds || 0))
    if (!enabled && this.fading) this.finishFade()
  }

  // Volume normalization (leveling): DynamicsCompressor after master.
  // Stored flag always wins eventually — applied now when the graph exists,
  // and (re)applied by ensureContext when it is (re)built. Bypass mode
  // (no AudioContext) keeps the flag but applies nothing.
  setNormalize(enabled: boolean) {
    this.normalizeEnabled = enabled
    this.applyNormalize()
  }

  isNormalize(): boolean {
    return this.normalizeEnabled
  }

  private setupAnalyser(ctx: AudioContextLike, master: GainNodeLike) {
    if (!ctx.createAnalyser) return
    try {
      const analyser = ctx.createAnalyser()
      analyser.fftSize = 512
      analyser.smoothingTimeConstant = 0.82
      const sink = ctx.createGain()
      sink.gain.value = 0
      analyser.connect(sink)
      sink.connect(ctx.destination)
      master.connect(analyser)
      this.analyser = analyser
    } catch {
      this.analyser = null
    }
  }

  getSpectrum(target: Uint8Array): boolean {
    if (!this.analyser || target.length === 0) return false
    try {
      this.analyser.getByteFrequencyData(target)
      return true
    } catch {
      return false
    }
  }

  isGraphReady(): boolean {
    return this.analyser !== null
  }

  private applyNormalize() {
    if (!this.ctx || !this.master) return
    try {
      this.master.disconnect()
      this.normalizer?.disconnect()
    } catch {
      /* already unwired — rewire below */
    }
    if (this.analyser) {
      try {
        this.master.connect(this.analyser)
      } catch {
        /* analyser tap is best-effort */
      }
    }
    if (this.normalizeEnabled) {
      const comp = this.normalizer ?? this.createNormalizer()
      if (!comp) {
        this.master.connect(this.ctx.destination)
        return
      }
      this.normalizer = comp
      this.master.connect(comp)
      comp.connect(this.ctx.destination)
    } else {
      this.master.connect(this.ctx.destination)
    }
  }

  private createNormalizer(): DynamicsCompressorLike | null {
    if (!this.ctx) return null
    try {
      const comp = this.ctx.createDynamicsCompressor()
      comp.threshold.value = -18
      comp.knee.value = 20
      comp.ratio.value = 3
      comp.attack.value = 0.003
      comp.release.value = 0.25
      return comp
    } catch {
      return null
    }
  }

  setNextProvider(
    peek: () => EngineTrack | null,
    begin: (expected: EngineTrack, outFile: string | null) => Promise<EngineTrack | null>,
  ) {
    this.peekNext = peek
    this.beginNext = begin
  }

  setDjCue(leadSeconds: number, cb: ((file: string, remaining: number) => void) | null) {
    this.djLeadSec = Number.isFinite(leadSeconds) ? Math.max(0, leadSeconds) : 0
    this.djCueCb = cb
  }

  onVoiceEnded(cb: () => void) {
    this.voiceEndedCb = cb
  }

  isVoicePlaying(): boolean {
    return this.voiceActive
  }

  playVoice(urls: string[], duckLevel = 0.18, onDone?: () => void) {
    const queue = urls.filter(Boolean)
    if (queue.length === 0) return
    this.ensureContext()
    this.voiceDoneCb = onDone ?? null
    this.voiceQueue = queue
    this.voiceDuckLevel = Math.min(0.8, Math.max(0.02, duckLevel))
    this.voiceActive = true
    this.applyDuck(true)
    this.playNextVoice()
  }

  private playNextVoice() {
    const url = this.voiceQueue.shift()
    if (!url) {
      this.finishVoice()
      return
    }
    try {
      this.voice.src = url
      this.voice.currentTime = 0
      this.voice.volume = this.master ? 1 : this.volume
      const p = this.voice.play()
      if (p && typeof (p as Promise<void>).catch === 'function') {
        void (p as Promise<void>).catch(() => this.handleVoiceEnded())
      }
    } catch {
      this.handleVoiceEnded()
    }
  }

  private handleVoiceEnded() {
    if (!this.voiceActive) return
    if (this.voiceQueue.length > 0) {
      this.playNextVoice()
      return
    }
    this.finishVoice()
  }

  private finishVoice() {
    if (!this.voiceActive) return
    const done = this.voiceDoneCb
    this.voiceDoneCb = null
    this.voiceActive = false
    this.voiceQueue = []
    try {
      this.voice.pause()
      this.voice.src = ''
    } catch {
      /* already gone */
    }
    this.applyDuck(false)
    this.voiceEndedCb()
    done?.()
  }

  pauseVoice() {
    if (!this.voiceActive) return
    try {
      this.voice.pause()
    } catch {
      /* ignore */
    }
  }

  resumeVoice() {
    if (!this.voiceActive) return
    try {
      const p = this.voice.play()
      if (p && typeof (p as Promise<void>).catch === 'function') {
        void (p as Promise<void>).catch(() => {})
      }
    } catch {
      /* ignore */
    }
  }

  stopVoice() {
    this.voiceQueue = []
    this.voiceDoneCb = null
    this.finishVoice()
  }

  private applyDuck(active: boolean) {
    const level = active ? this.voiceDuckLevel : 1
    if (this.musicBus) {
      const gain = this.musicBus.gain
      if (typeof gain.setTargetAtTime === 'function' && this.ctx?.currentTime !== undefined) {
        gain.setTargetAtTime(level, this.ctx.currentTime, 0.08)
      } else {
        gain.value = level
      }
    } else {
      const music = this.volume * level
      this.a.el.volume = music
      this.b.el.volume = music
    }
    if (!this.master) this.voice.volume = active ? this.volume : 0
  }

  onEnded(cb: () => void) {
    this.endedCb = cb
  }

  // Buffering/reconnecting indicator (debounced by the caller's UI).
  onBuffering(cb: (buffering: boolean) => void) {
    this.bufferingCb = cb
  }

  // A file that cannot be decoded/streamed even after fresh-URL retries.
  // The store advances the queue instead of freezing on it.
  onFatal(cb: (file: string, reason: string) => void) {
    this.fatalCb = cb
  }

  // Recovery URL provider: refresh access tokens before re-attaching the
  // stream. Media requests bypass the API client's 401 refresh path.
  setStreamUrlProvider(fn: (file: string, isPodcast: boolean) => string | Promise<string>) {
    this.streamUrl = fn
  }

  // Fired when the track strands at its tail: the final bytes are
  // unplayable, `ended` will never fire, and the recovery budget is spent.
  // The store treats it as finished (advance, or replay when repeat is on)
  // instead of freezing the bar a second short forever.
  onTailStuck(cb: () => void) {
    this.tailStuckCb = cb
  }

  // Media time actually observed playing (recovery high-water mark). Used
  // by the store's instant-death breaker to tell a genuine finish from a
  // file that died moments after loading.
  lastGoodPosition(): number {
    return this.lastGoodTime
  }

  // ---- transport -----------------------------------------------------

  private get idle(): Slot {
    return this.active === this.a ? this.b : this.a
  }

  private get graph(): boolean {
    return this.graphOk && this.ctx !== null && this.master !== null
  }

  // Must be called from a user gesture at least once (autoplay policy).
  ensureContext() {
    if (this.ctx) {
      if (this.ctx.state === 'suspended') void this.ctx.resume()
      return
    }
    try {
      const ctx = this.deps.createContext()
      const master = ctx.createGain()
      master.gain.value = this.volume
      for (const slot of [this.a, this.b]) {
        const gain = ctx.createGain()
        gain.gain.value = slot === this.active ? 1 : 0
        ctx.createMediaElementSource(slot.el).connect(gain)
        slot.gain = gain
      }
      const musicBus = ctx.createGain()
      musicBus.gain.value = 1
      musicBus.connect(master)
      for (const slot of [this.a, this.b]) slot.gain?.connect(musicBus)
      const voiceGain = ctx.createGain()
      voiceGain.gain.value = 1
      ctx.createMediaElementSource(this.voice).connect(voiceGain)
      voiceGain.connect(master)
      this.ctx = ctx
      this.master = master
      this.musicBus = musicBus
      this.voice.volume = 1
      this.setupAnalyser(ctx, master)
      this.applyNormalize()
      if (ctx.state === 'suspended') void ctx.resume()
      if (!this.contextReported) {
        this.contextReported = true
        this.reportOnce(
          `audio graph ready rate=${ctx.sampleRate ?? '?'}Hz ` +
            `base=${ctx.baseLatency ?? '?'}s out=${ctx.outputLatency ?? '?'}s state=${ctx.state}`,
        )
      }
    } catch {
      // No Web Audio: bypass mode (plain elements, no fades).
      this.graphOk = false
      this.a.el.volume = this.volume
      this.b.el.volume = this.volume
      if (!this.contextReported) {
        this.contextReported = true
        this.reportOnce('audio graph unavailable, using plain element playback')
      }
    }
  }

  load(track: EngineTrack | null, autoplay: boolean, restart = false) {
    this.ensureContext()
    this.cancelFade()
    this.cancelRetry(true)
    this.wantPlaying = autoplay
    this.lastDjCueFile = null
    if (!track) {
      this.wantPlaying = false
      this.stopVoice()
      this.active.el.pause()
      this.active.el.src = ''
      this.active.file = null
      this.active.url = null
      this.lastGoodTime = 0
      this.lastRecoveryAt = -Infinity
      this.lastUserSeekAt = -Infinity
      this.lastTailAdvanceAt = -Infinity
      this.lastSeekAt = -Infinity
      this.stopTimer()
      return
    }
    if (this.active.file !== track.file) {
      this.active.el.src = track.url
      try {
        this.active.el.currentTime = 0
      } catch (e: unknown) {
        // No data yet (slow storage): the element recovers on metadata.
        this.report(`load reset currentTime threw: ${e instanceof Error ? e.message : String(e)}`)
      }
      this.active.file = track.file
      this.active.url = track.url
      this.active.isPodcast = track.isPodcast
      this.active.baseGain = gainForDb(track.gainDb)
      this.active.recoveries = 0
      this.lastGoodTime = 0
      this.lastRecoveryAt = -Infinity
      this.lastUserSeekAt = -Infinity
      this.lastTailAdvanceAt = -Infinity
      this.lastSeekAt = -Infinity
      this.lastLoadAt = this.deps.now()
    } else if (restart) {
      try {
        this.active.el.currentTime = 0
      } catch (e: unknown) {
        this.report(`load restart currentTime threw: ${e instanceof Error ? e.message : String(e)}`)
      }
      this.active.recoveries = 0
      this.lastGoodTime = 0
      this.lastRecoveryAt = -Infinity
      this.lastUserSeekAt = -Infinity
      this.lastTailAdvanceAt = -Infinity
      this.lastSeekAt = -Infinity
      this.lastLoadAt = this.deps.now()
    } else if (autoplay) {
      // Explicit (re)start of the same file (resume after a fatal error,
      // repeat-one, remote play): grant a fresh recovery budget.
      this.active.recoveries = 0
      this.lastRecoveryAt = -Infinity
      this.lastUserSeekAt = -Infinity
    }
    this.setSlotGain(this.active, 1)
    if (autoplay) this.tryPlay(this.active.el, 'load')
    else this.active.el.pause()
    this.lastAdvanceAt = this.deps.now()
    this.startTimer()
  }

  play() {
    this.ensureContext()
    this.wantPlaying = true
    this.startTimer()
    this.lastAdvanceAt = this.deps.now()
    // Resuming a paused fade shifts its start so progress freezes cleanly.
    if (this.fading && this.fading.pauseBeganAt !== null) {
      this.fading.start += this.deps.now() - this.fading.pauseBeganAt
      this.fading.pauseBeganAt = null
    }
    this.tryPlay(this.active.el, 'play')
    if (this.fading) this.tryPlay(this.fading.incoming.el, 'play-fade-incoming')
    this.resumeVoice()
  }

  pause() {
    this.wantPlaying = false
    this.cancelRetry(true)
    if (this.fading && this.fading.pauseBeganAt === null) this.fading.pauseBeganAt = this.deps.now()
    this.active.el.pause()
    if (this.fading) this.fading.incoming.el.pause()
    this.pauseVoice()
  }

  seek(sec: number, isUserSeek = true) {
    if (this.fading) this.cancelToIncoming()
    // Explicit user seeks (slider, chapters, arrows, resume) grant a fresh
    // recovery budget: the user deliberately picked a new spot, so past
    // failures elsewhere must not instantly pause them. System seeks (drift
    // correction) pass false — they must never mint fresh budget, or the
    // recovery/drift loop would spin forever instead of pausing bounded.
    if (isUserSeek) {
      this.active.recoveries = 0
      this.lastUserSeekAt = this.deps.now()
      // Deliberate intent: drop any pending network retry so it cannot
      // yank playback back to the pre-seek high-water mark.
      this.cancelRetry(true)
    }
    // Clamp into playable range (element duration is the ground truth —
    // metadata estimates can overshoot it on huge VBR files, and landing
    // on/past duration fires `ended` and skips away from the episode).
    // lastGoodTime deliberately keeps the last *actually playing* position
    // (updated by the tick): error recovery returns there, not to a seek
    // target that may itself be poisoned.
    const target = clampSeekTarget(sec, this.active.el.duration)
    this.lastSeekAt = this.deps.now()
    this.lastSeekTarget = target
    this.lastAdvanceAt = this.deps.now()
    this.report(
      `seek req=${sec} target=${target} dur=${this.active.el.duration} ${this.describeSlot(this.active)}`,
      true,
    )
    try {
      this.active.el.currentTime = target
    } catch (e: unknown) {
      this.report(
        `seek set currentTime threw target=${target}: ${e instanceof Error ? e.message : String(e)}`,
      )
    }
  }

  // ---- read model (drives progress bars, lyrics, MediaSession) --------

  time(): number {
    // Reads can throw when the element unloaded mid-seek (the long-file
    // stall): guard them, or one bad read kills the caller's ticker.
    try {
      const t = this.active.el.currentTime
      return Number.isFinite(t) ? Math.max(0, t) : 0
    } catch (e: unknown) {
      this.report(`time() read threw: ${e instanceof Error ? e.message : String(e)}`)
      return 0
    }
  }

  duration(): number {
    try {
      const d = this.active.el.duration
      return Number.isFinite(d) && d > 0 ? d : 0
    } catch (e: unknown) {
      this.report(`duration() read threw: ${e instanceof Error ? e.message : String(e)}`)
      return 0
    }
  }

  currentFile(): string | null {
    return this.active.file
  }

  // Drift-correction target for a server position, or null when no
  // correction is needed. Owns both caps so callers cannot re-poison the
  // element by hand:
  // - duration-edge cap: landing on the edge fires `ended` and advances
  //   away mid-track (server wall-clock estimates outrun stalled elements).
  // - recovery ceiling: after any recovery ran for the loaded file, a
  //   forward jump past lastGoodTime (the observed-playable high-water
  //   mark) re-enters the region that already stalled — the slider-seek
  //   freeze (frozen bar, dead toggle). Capped back to the ceiling; null
  //   when already there. Fresh files (no recovery yet) are unaffected, so
  //   refresh-resume still jumps forward normally. User seeks never consult
  //   this (they call seek() directly), so retrying a spot stays possible.
  driftTarget(serverPos: number, catalogDuration: number): number | null {
    if (this.fading) return null
    if (!Number.isFinite(serverPos) || serverPos < 0) return null
    let t: number
    try {
      t = this.active.el.currentTime
    } catch {
      return null
    }
    if (!Number.isFinite(t) || t < 0 || Math.abs(t - serverPos) <= 2) return null
    const elDur = this.duration()
    const known = elDur > 0 ? elDur : catalogDuration
    const cap = known > DRIFT_EDGE_MARGIN_SEC + 1 ? known - DRIFT_EDGE_MARGIN_SEC : known
    const target = known > 0 ? Math.min(serverPos, cap) : serverPos
    if (
      target > t &&
      this.lastRecoveryAt > this.lastLoadAt &&
      this.lastUserSeekAt <= this.lastRecoveryAt &&
      target > this.lastGoodTime + 2
    ) {
      const ceiling = Math.max(0, this.lastGoodTime)
      this.report(
        `drift forward suppressed (server=${serverPos} ceiling=${ceiling} dur=${elDur}) ${this.describeSlot(this.active)}`,
      )
      if (Math.abs(t - ceiling) <= 2) return null
      return ceiling
    }
    return target
  }

  // File the in-progress fade is rendering (null when not fading).
  fadeTarget(): string | null {
    return this.fading?.incoming.file ?? null
  }

  isPlaying(): boolean {
    if (!this.active.el.paused) return true
    return this.fading !== null && !this.fading.incoming.el.paused
  }

  isFading(): boolean {
    return this.fading !== null
  }

  // ---- internals ------------------------------------------------------

  private setSlotGain(slot: Slot, v: number) {
    if (slot.gain) slot.gain.gain.value = v * slot.baseGain
    else if (!this.graph) {
      slot.el.volume =
        this.active === slot ? Math.min(1, Math.max(0, this.volume * slot.baseGain)) : 0
    }
  }

  private handleEnded(slot: Slot) {
    this.lastDjCueFile = null
    if (this.fading) {
      // Outgoing track hit its real end mid-fade: settle immediately.
      if (slot === this.fading.out) this.finishFade()
      return
    }
    // A paused element never resumes on `ended` — park inside even when
    // the end looks spurious (seek+pause near EOF). Checked first so a
    // seek-then-pause can't resurrect audio.
    if (slot === this.active && this.readPaused(slot.el)) {
      this.report(`paused ended ignored, parking inside ${this.describeSlot(slot)}`)
      this.parkInside(slot)
      return
    }
    if (slot === this.active && this.isSpuriousEnd()) {
      // A seek just landed us here but the target was safely inside the
      // track (bad byte range past EOF on a huge file): re-seek the target
      // instead of advancing away from the user's episode. `ended` only
      // fires out of playing state, so resuming here is always correct.
      // The guard grants one recovery per seek: if the target itself is
      // past a cut point, the next `ended` falls through to the cut-short
      // path below instead of re-seeking forever.
      // (Paused case already returned above — reaching here means playing.)
      this.lastSeekAt = -Infinity
      this.report(
        `spurious ended ignored target=${this.lastSeekTarget} dur=${slot.el.duration} ${this.describeSlot(slot)}`,
      )
      try {
        slot.el.currentTime = this.lastSeekTarget
        this.tryPlay(slot.el, 'spurious-end')
        return
      } catch (e: unknown) {
        this.report(`spurious-end recovery threw: ${e instanceof Error ? e.message : String(e)}`)
        /* fall through to normal handling below */
      }
    }
    if (slot === this.active) {
      // Paused case already handled above (early return covers both
      // spurious and edge parks).
      // `ended` far from the known end (cut-short stream, truncated file):
      // the track didn't really finish. Recover like an element error
      // instead of advancing away mid-song. Bounded — a deterministically
      // cut file pauses instead of spinning or skipping.
      if (this.isCutShortEnd()) {
        this.report(`cut-short ended, recovering ${this.describeSlot(slot)}`)
        this.recoverCutShortEnd(slot)
        return
      }
      this.report(`ended, advancing ${this.describeSlot(slot)}`)
      this.endedCb()
    }
  }

  // True when `ended` fired well short of a sane known duration — i.e. the
  // audio cut out mid-track, not a real finish. Unknown/tiny durations
  // (corrupt or empty files) keep the legacy advance behavior.
  private isCutShortEnd(): boolean {
    try {
      const dur = this.active.el.duration
      const pos = this.active.el.currentTime
      if (!Number.isFinite(dur) || dur <= CUT_SHORT_MIN_DURATION_SEC) return false
      if (!Number.isFinite(pos) || pos < 0) return false
      return dur - pos > CUT_SHORT_GAP_SEC
    } catch {
      return false
    }
  }

  private recoverCutShortEnd(slot: Slot) {
    if (slot.recoveries >= FATAL_MEDIA_RETRIES) {
      this.handleExhausted(slot, 'cut-short')
      return
    }
    slot.recoveries += 1
    this.report(`cut-short ended, retrying (#${slot.recoveries}) ${this.describeSlot(slot)}`)
    this.scheduleRetry(slot, `cut-short-${slot.recoveries}`)
  }

  private readPaused(el: AudioElementLike): boolean {
    try {
      return el.paused
    } catch {
      return false // unreadable: keep the legacy advance path
    }
  }

  // Back off the duration edge after a paused `ended` (see handleEnded):
  // re-seek the current position, which clampSeekTarget pulls inside when
  // it sits on/past the edge and leaves alone otherwise. Never resumes —
  // the user had it paused.
  private parkInside(slot: Slot) {
    let pos = 0
    let ok = false
    try {
      pos = slot.el.currentTime
      ok = Number.isFinite(pos) && pos >= 0
    } catch {
      ok = false
    }
    if (ok) this.seek(pos, false)
  }

  // True when `ended` fired suspiciously soon after a seek whose target was
  // safely inside the known duration — i.e. not a real finish.
  private isSpuriousEnd(): boolean {    if (this.deps.now() - this.lastSeekAt > SPURIOUS_END_WINDOW_MS) return false
    const dur = this.active.el.duration
    if (!Number.isFinite(dur) || dur <= 0) return false
    return this.lastSeekTarget < dur - 2
  }

  // Element-level failures. Network-class errors (and the watchdog's null
  // error) retry forever with backoff while playback is intended.
  // Deterministic decode/source failures get a few fresh-URL attempts, then
  // hand off to the store so it can advance past the broken file.
  private handleSlotError(slot: Slot) {
    const transient = this.isTransientMediaError(slot)
    if (slot !== this.active) {
      // Already cleared for a fresh preload: ignore late error events.
      if (slot.file === null) {
        slot.recoveries = 0
        return
      }
      // Staged/preloaded slot failed: refresh its URL and re-attach. If it
      // exhausts the bounded budget mid-fade, promote it (the store already
      // names it) and let the active retry path own recovery. Never abort
      // silently: the outgoing tail would then advance the server twice.
      if (slot.recoveries >= MAX_RECOVERIES) {
        slot.recoveries = 0
        if (this.fading?.incoming === slot) {
          this.finishFade()
          if (this.wantPlaying) this.scheduleRetry(this.active, 'fade-error-exhausted')
        } else {
          slot.file = null
          slot.url = null
        }
        return
      }
      slot.recoveries += 1
      const wasPlaying = !this.readPaused(slot.el)
      this.report(`slot error, re-attaching (#${slot.recoveries}) ${this.describeSlot(slot)}`)
      void this.reattachSlot(slot, wasPlaying)
      return
    }
    if (!this.wantPlaying) {
      this.setBuffering(false)
      return
    }
    if (transient) {
      this.report(`element error (retrying): ${this.describeSlot(slot)}`)
      // Hold the element during backoff: a glitched clock reading must not
      // count as healthy progress and cancel the pending retry. Aborted
      // errors are usually our own src swap — leave those alone.
      if (this.mediaErrorCode(slot) !== 1) this.pauseQuietly(slot.el)
      this.scheduleRetry(slot, 'element-error')
      return
    }
    if (slot.recoveries >= FATAL_MEDIA_RETRIES) {
      this.handleFatalMedia(slot, `media-error-${this.mediaErrorCode(slot) ?? 'unknown'}`)
      return
    }
    slot.recoveries += 1
    this.report(
      `element fatal error #${slot.recoveries}, retrying with a fresh URL ${this.describeSlot(slot)}`,
    )
    this.scheduleRetry(slot, `media-error-${slot.recoveries}`)
  }

  // Re-attach a staged (preloaded / fade-incoming) slot with a fresh URL.
  // Stream URLs carry access tokens, so a long preload window can expire
  // them; refreshing here keeps the fade from handing over to a dead URL.
  private async reattachSlot(slot: Slot, wasPlaying: boolean) {
    let url = slot.url ?? ''
    if (this.streamUrl && slot.file) {
      try {
        const fresh = await this.streamUrl(slot.file, slot.isPodcast)
        if (fresh) url = fresh
      } catch (e: unknown) {
        this.report(`slot URL refresh failed: ${e instanceof Error ? e.message : String(e)}`)
      }
    }
    // The slot was repurposed or promoted to active while resolving:
    // whichever path owns it now is responsible for its recovery.
    if (slot === this.active || slot !== this.idle) return
    try {
      if (url) {
        slot.url = url
        slot.el.src = url
        slot.el.load()
      }
      slot.el.currentTime = 0
      // Idle preloads stay paused; only a live fade incoming resumes, and
      // only if playback is still intended (a user pause wins).
      if (wasPlaying && this.wantPlaying && this.fading?.incoming === slot) {
        this.tryPlay(slot.el, 'fade-error-recovery')
      }
    } catch (e: unknown) {
      this.report(`slot recovery threw: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  // True when the strand sits at the track's tail: played up to within a
  // few seconds of a known duration, so the final bytes (not the middle)
  // are unplayable. Only observed media time counts — a glitched clock
  // reading must never pass a mid-track stall off as finished.
  private strandIsTail(): boolean {
    const d = this.duration()
    if (!(d > CUT_SHORT_MIN_DURATION_SEC)) return false
    return this.lastGoodTime >= d - TAIL_STUCK_WINDOW_SEC
  }

  // Natural finish already fired: don't schedule a load retry while the
  // store's auto-advance is in flight.
  private atEnd(): boolean {
    try {
      const d = this.active.el.duration
      const t = this.active.el.currentTime
      return Number.isFinite(d) && d > 0 && Number.isFinite(t) && t >= d - END_EPSILON_SEC
    } catch {
      return false
    }
  }

  // Shared bounded-recovery exhaustion path. A strand at the tail while
  // trying to play means the track is over as far as the bytes are
  // concerned (`ended` will never fire): fire the tail callback so the
  // queue advances instead of freezing a second short forever. Mid-track
  // deterministic failures hand off to the store for auto-skip instead of
  // pausing with a UI that still claims to be playing.
  private handleExhausted(slot: Slot, kind: 'cut-short' | 'error' | 'unload') {
    if (
      this.strandIsTail() &&
      this.deps.now() - this.lastTailAdvanceAt > TAIL_STUCK_COOLDOWN_MS
    ) {
      this.lastTailAdvanceAt = this.deps.now()
      slot.recoveries = 0
      this.report(`tail stuck, advancing (${kind}) ${this.describeSlot(slot)}`)
      try {
        slot.el.pause()
      } catch {
        /* ignore */
      }
      this.tailStuckCb()
      return
    }
    if (slot === this.active && this.wantPlaying) {
      this.handleFatalMedia(slot, `${kind}-exhausted`)
      return
    }
    this.report(`${kind} recovery exhausted, pausing ${this.describeSlot(slot)}`)
    try {
      slot.el.pause()
    } catch {
      /* ignore */
    }
  }

  private cancelFade() {
    if (!this.fading) return
    this.fading.incoming.el.pause()
    this.setSlotGain(this.fading.incoming, 0)
    this.setSlotGain(this.active, 1)
    this.fading = null
  }

  // Abort a fade but keep the *incoming* (displayed) track: used by seeks,
  // which target whatever the UI is showing.
  private cancelToIncoming() {
    if (!this.fading) return
    const incoming = this.fading.incoming
    this.fading.out.el.pause()
    this.setSlotGain(this.fading.out, 0)
    this.setSlotGain(incoming, 1)
    this.active = incoming
    this.fading = null
    this.adoptIncoming()
  }

  private finishFade() {
    if (!this.fading) return
    const incoming = this.fading.incoming
    this.fading.out.el.pause()
    try {
      this.fading.out.el.currentTime = 0
    } catch {
      /* ignore */
    }
    this.setSlotGain(this.fading.out, 0)
    this.setSlotGain(incoming, 1)
    this.active = incoming
    this.fading = null
    this.adoptIncoming()
  }

  // The rendered track just changed slots (fade completed or seek cut over
  // mid-fade): restart stall tracking from the incoming stream's own clock.
  // Without this, lastGoodTime/lastAdvanceAt still describe the OUTGOING
  // track, so the first watchdog window after the fade "recovers" the new
  // song to the old song's position — a deterministic jump to nearly the
  // end a few seconds after every transition.
  private adoptIncoming() {
    let t = 0
    try {
      const cur = this.active.el.currentTime
      if (Number.isFinite(cur) && cur >= 0) t = cur
    } catch {
      /* unreadable clock: era starts at 0 */
    }
    this.lastGoodTime = t
    this.lastAdvanceAt = this.deps.now()
    this.lastLoadAt = this.deps.now()
    this.lastRecoveryAt = -Infinity
    this.cancelRetry(true)
    this.active.recoveries = 0
  }

  // Stream unloaded underneath us (EMPTY/NO_SOURCE with a file assigned).
  private isUnloaded(slot: Slot): boolean {
    try {
      const net = slot.el.networkState
      return net === 0 || net === 3
    } catch {
      return false
    }
  }

  // Stream unloaded underneath us: retry forever while playback is intended
  // (the URL provider refreshes expired tokens on every attempt).
  private recoverUnload(slot: Slot) {
    this.pauseQuietly(slot.el)
    this.scheduleRetry(slot, 'unload')
  }

  // Frozen-clock watchdog: a playing element whose time stops advancing
  // (stalled range request on a huge file, no error event) gets a retry.
  // The error listener covers hard failures. Seeking state never counts as
  // stalled — the clock legitimately holds still mid-seek.
  private watchStall() {
    const el = this.active.el
    let t: number
    try {
      t = el.currentTime
    } catch {
      return
    }
    const now = this.deps.now()
    if (el.seeking || !Number.isFinite(t) || t < 0) return
    if (t > this.lastGoodTime) {
      this.lastGoodTime = t
      this.lastAdvanceAt = now
      // Playback (or a retry) is working again: drop any pending retry
      // before it interrupts healthy audio, and relax the backoff after a
      // sustained healthy stretch.
      if (this.retryTimer !== null) this.cancelRetry(false)
      this.retrying = false
      this.setBuffering(false)
      if (now - this.lastRetryAt > HEALTHY_RESET_MS) {
        this.retryAttempt = 0
        this.lastRetryAt = -Infinity
      }
      return
    }
    // No media-time progress for a moment: show buffering instead of a
    // frozen bar. The retry itself waits for the longer stall timeout.
    if (this.wantPlaying && now - this.lastAdvanceAt > BUFFERING_AFTER_MS) {
      this.setBuffering(true)
    }
    if (now - this.lastAdvanceAt > STALL_TIMEOUT_MS) {
      const frozenSec = Math.round((now - this.lastAdvanceAt) / 1000)
      this.lastAdvanceAt = now
      this.report(`stall watchdog fired (frozen ~${frozenSec}s): ${this.describeSlot(this.active)}`)
      this.scheduleRetry(this.active, 'stall-watchdog')
    }
  }

  private startTimer() {
    if (this.timer !== null) return;
    this.timer = setInterval(() => {
      try {
        this.tick()
      } catch {
        /* the ticker must never break playback */
      }
    }, TICK_MS)
  }

  private stopTimer() {
    if (this.timer !== null) {
      clearInterval(this.timer)
      this.timer = null
    }
  }

  private maybeCueDj() {
    if (!this.djCueCb || this.djLeadSec <= 0 || !this.active.file) return
    const dur = this.active.el.duration
    const pos = this.active.el.currentTime
    if (!Number.isFinite(dur) || dur <= 1 || !Number.isFinite(pos) || pos < 0.5) return
    const remaining = dur - pos
    if (remaining > this.djLeadSec || remaining <= 0) return
    if (this.lastDjCueFile === this.active.file) return
    this.lastDjCueFile = this.active.file
    this.djCueCb(this.active.file, remaining)
  }

  private tick() {
    // Fully idle (nothing loaded, no fade): park the ticker instead of
    // spinning every 250ms for the lifetime of the page. play()/load()
    // restart it on demand.
    if (!this.fading && !this.active.file) {
      this.stopTimer()
      return
    }
    // Unloaded stream (the suspected long-file killer): EMPTY/NO_SOURCE
    // with a file still assigned, past the post-load/seek grace. A
    // user-paused element keeps its resource, so this only trips when
    // playback is intended. Recovery re-attaches with a fresh URL.
    if (this.wantPlaying && !this.retrying && this.active.file !== null && this.isUnloaded(this.active)) {
      const now = this.deps.now()
      if (now - this.lastLoadAt > UNLOAD_GRACE_MS && now - this.lastSeekAt > UNLOAD_GRACE_MS) {
        this.lastLoadAt = now
        this.recoverUnload(this.active)
        return
      }
    }
    if (!this.active.el || this.active.el.paused) {
      // The element paused itself without a user pause (failed load, lost
      // stream, exhausted legacy path). Keep the intent alive and retry.
      if (
        this.wantPlaying &&
        !this.retrying &&
        this.active.file !== null &&
        !this.atEnd() &&
        this.deps.now() - Math.max(this.lastLoadAt, this.lastRetryRanAt) > UNLOAD_GRACE_MS
      ) {
        this.scheduleRetry(this.active, 'element-paused')
      }
      // Paused (or nothing loaded): freeze fade progress. While a fade is
      // paused only the *incoming* may still be running (autoplay race) —
      // hold it too so both resume in sync.
      if (this.fading && !this.fading.incoming.el.paused && this.active.el.paused) {
        this.fading.incoming.el.pause()
        if (this.fading.pauseBeganAt === null) this.fading.pauseBeganAt = this.deps.now()
      }
      return
    }
    // Silent-stall watchdog (skipped mid-fade so ramps keep their timing).
    if (!this.fading) this.watchStall()
    if (!this.fading) this.maybeCueDj()
    if (this.fading) {
      const f = this.fading
      const elapsed = (this.deps.now() - f.start) / 1000
      const t = Math.min(1, elapsed / f.dur)
      const [out, inp] = fadeGains(t)
      this.setSlotGain(f.out, out)
      this.setSlotGain(f.incoming, inp)
      if (t >= 1) this.finishFade()
      return
    }
    if (!this.fadeEnabled || !this.graph) return
    const dur = this.active.el.duration
    const pos = this.active.el.currentTime
    if (!Number.isFinite(dur) || dur <= 1 || !Number.isFinite(pos) || pos < 0.5) return
    const remaining = dur - pos
    const next = this.peekNext()
    if (!next || next.file === this.active.file) return
    if (this.active.isPodcast || next.isPodcast) return // podcasts never fade
    const idle = this.idle
    if (idle.file !== next.file) {
      // Stage the incoming track early so its bytes are buffered. When
      // already inside the fade window, stage and start in the same tick.
      if (remaining <= this.fadeSec + PRELOAD_AHEAD_SEC) {
        idle.el.src = next.url
        idle.file = next.file
        idle.isPodcast = next.isPodcast
        idle.baseGain = gainForDb(next.gainDb)
        // Fresh stream, fresh budget: errors from whatever this slot held
        // two tracks ago must not exhaust the incoming on arrival.
        idle.recoveries = 0
      }
      if (remaining > this.fadeSec) return
    }
    if (remaining <= this.fadeSec) void this.startFade(next)
  }

  private async startFade(next: EngineTrack) {
    // Never start a fade from a paused element: pausing inside the fade
    // window must stay silent instead of waking queue[0].
    if (this.readPaused(this.active.el)) return
    const idle = this.idle
    // A reused slot may carry an exhausted budget from a previous life
    // (cancelled/aborted fade); the staged stream always starts fresh.
    idle.recoveries = 0
    const dur = this.active.el.duration
    const effective = Math.min(this.fadeSec, Math.max(1, dur - this.active.el.currentTime))
    try {
      idle.el.currentTime = 0
    } catch {
      /* ignore */
    }
    this.setSlotGain(idle, 0)
    try {
      await idle.el.play()
    } catch {
      return // incoming wouldn't start — stay on the current track
    }
    // The pause may have landed while `play()` was in flight: the tick
    // froze the active slot but the incoming is now audible. Hold it and
    // drop the fade instead of resurrecting playback after a pause.
    if (this.readPaused(this.active.el)) {
      try {
        idle.el.pause()
      } catch {
        /* ignore */
      }
      return
    }
    if (this.fading || this.active.file === next.file) {
      idle.el.pause()
      return
    }
    this.fading = {
      out: this.active,
      incoming: idle,
      start: this.deps.now(),
      dur: effective,
      pauseBeganAt: null,
    }
    // Advance the server exactly when the new audio starts, so its position
    // estimate and the new track stay aligned. The outgoing file travels
    // along so a queue that moved under the fade (manual transport in the
    // preload/fade window) converges instead of advancing a second time.
    // Any other change (remote edit mid-fade) hard-switches to whatever
    // the server says is current.
    const outFile = this.fading.out.file
    void this.beginNext(next, outFile)
      .then((actual) => {
        if (!this.fading || !actual || actual.file !== idle.file) {
          if (this.fading && actual && actual.file !== idle.file) this.abortTo(actual)
        }
      })
      .catch(() => {
        if (this.fading) this.abortTo(null)
      })
  }

  // Queue changed under a fade (or advance failed): drop the fade and play
  // whatever was requested — or just stop the incoming on total failure.
  // Never resumes paused audio: capture play state before tearing down,
  // then swap silently when the user had it paused.
  private abortTo(actual: EngineTrack | null) {
    if (!this.fading) return
    const incoming = this.fading.incoming
    const wasPlaying = !this.readPaused(this.active.el) || !this.readPaused(incoming.el)
    incoming.el.pause()
    this.setSlotGain(incoming, 0)
    this.fading = null
    if (actual && this.active.file !== actual.file) {
      this.cancelRetry(true)
      this.active.el.src = actual.url
      this.active.el.currentTime = 0
      this.active.file = actual.file
      this.active.url = actual.url
      this.active.isPodcast = actual.isPodcast
      this.active.baseGain = gainForDb(actual.gainDb)
      this.active.recoveries = 0
      this.lastGoodTime = 0
      this.lastRecoveryAt = -Infinity
      this.lastUserSeekAt = -Infinity
      this.lastTailAdvanceAt = -Infinity
      this.lastSeekAt = -Infinity
      this.lastLoadAt = this.deps.now()
      this.setSlotGain(this.active, 1)
      if (wasPlaying) this.tryPlay(this.active.el, 'abortTo')
      else {
        try {
          this.active.el.pause()
        } catch {
          /* keep paused */
        }
      }
    } else {
      this.setSlotGain(this.active, 1)
    }
  }
}

// App-wide singleton (the store and components share it).
export const engine = new AudioEngine()
