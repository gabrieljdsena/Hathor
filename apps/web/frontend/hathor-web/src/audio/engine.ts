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

export interface AudioContextLike {
  readonly destination: unknown
  readonly state: string
  createGain(): GainNodeLike
  createDynamicsCompressor(): DynamicsCompressorLike
  createMediaElementSource(el: AudioElementLike): AudioSourceLike
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
  private a: Slot
  private b: Slot
  private active: Slot
  private fading: Fade | null = null
  private volume = 0.7
  private fadeEnabled = false
  private fadeSec = 5
  private peekNext: () => EngineTrack | null = () => null
  private beginNext: (expected: EngineTrack, outFile: string | null) => Promise<EngineTrack | null> =
    async () => null
  private endedCb: () => void = () => {}
  private timer: ReturnType<typeof setInterval> | null = null
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

  private tryPlay(el: AudioElementLike, why: string) {
    try {
      const p = el.play() as Promise<void> | undefined
      if (p && typeof p.catch === 'function') {
        p.catch((e: unknown) => {
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
  private lastAdvanceAt = 0
  private lastSeekAt = -Infinity
  private lastSeekTarget = 0
  private lastLoadAt = -Infinity

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
      (() => new AudioContext() as unknown as AudioContextLike)
    this.deps = {
      createElement,
      createContext: contextFactory,
      now: deps?.now ?? (() => performance.now()),
    }
    const mkSlot = (): Slot => ({ el: this.deps.createElement(), gain: null, file: null, url: null, isPodcast: false, baseGain: 1, recoveries: 0 })
    this.a = mkSlot()
    this.b = mkSlot()
    this.active = this.a
    this.a.el.addEventListener('ended', () => this.handleEnded(this.a))
    this.b.el.addEventListener('ended', () => this.handleEnded(this.b))
    this.a.el.addEventListener('error', () => this.handleSlotError(this.a))
    this.b.el.addEventListener('error', () => this.handleSlotError(this.b))
  }

  // ---- configuration -------------------------------------------------

  setVolume(v: number) {
    if (!Number.isFinite(v)) return
    this.volume = v
    if (this.master) this.master.gain.value = v
    else {
      // Bypass mode: scale the elements directly.
      this.a.el.volume = v
      this.b.el.volume = v
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

  private applyNormalize() {
    if (!this.ctx || !this.master) return
    try {
      this.master.disconnect()
      this.normalizer?.disconnect()
    } catch {
      /* already unwired — rewire below */
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

  onEnded(cb: () => void) {
    this.endedCb = cb
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
        gain.connect(master)
        slot.gain = gain
      }
      this.ctx = ctx
      this.master = master
      this.applyNormalize()
      if (ctx.state === 'suspended') void ctx.resume()
    } catch {
      // No Web Audio: bypass mode (plain elements, no fades).
      this.graphOk = false
      this.a.el.volume = this.volume
      this.b.el.volume = this.volume
    }
  }

  load(track: EngineTrack | null, autoplay: boolean) {
    this.ensureContext()
    this.cancelFade()
    if (!track) {
      this.active.el.pause()
      this.active.el.src = ''
      this.active.file = null
      this.active.url = null
      this.lastGoodTime = 0
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
      this.lastSeekAt = -Infinity
      this.lastLoadAt = this.deps.now()
    }
    this.setSlotGain(this.active, 1)
    if (autoplay) this.tryPlay(this.active.el, 'load')
    else this.active.el.pause()
    this.lastAdvanceAt = this.deps.now()
    this.startTimer()
  }

  play() {
    this.ensureContext()
    this.startTimer()
    this.lastAdvanceAt = this.deps.now()
    // Resuming a paused fade shifts its start so progress freezes cleanly.
    if (this.fading && this.fading.pauseBeganAt !== null) {
      this.fading.start += this.deps.now() - this.fading.pauseBeganAt
      this.fading.pauseBeganAt = null
    }
    this.tryPlay(this.active.el, 'play')
    if (this.fading) this.tryPlay(this.fading.incoming.el, 'play-fade-incoming')
  }

  pause() {    if (this.fading && this.fading.pauseBeganAt === null) this.fading.pauseBeganAt = this.deps.now()
    this.active.el.pause()
    if (this.fading) this.fading.incoming.el.pause()
  }

  seek(sec: number) {
    if (this.fading) this.cancelToIncoming()
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
    if (this.fading) {
      // Outgoing track hit its real end mid-fade: settle immediately.
      if (slot === this.fading.out) this.finishFade()
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
    if (slot.recoveries >= MAX_RECOVERIES) {
      this.report(`cut-short recovery exhausted, pausing ${this.describeSlot(slot)}`)
      try {
        slot.el.pause()
      } catch {
        /* ignore */
      }
      return
    }
    slot.recoveries += 1
    try {
      slot.el.currentTime = Math.max(0, this.lastGoodTime)
      this.lastAdvanceAt = this.deps.now()
      // `ended` fires out of playing state (see the spurious branch), so
      // resuming here is always correct. lastSeekAt is deliberately left
      // alone: the spurious guard keeps its per-seek semantics.
      this.tryPlay(slot.el, 'cut-short-end')
    } catch (e: unknown) {
      this.report(`cut-short recovery threw: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  // True when `ended` fired suspiciously soon after a seek whose target was
  // safely inside the known duration — i.e. not a real finish.
  private isSpuriousEnd(): boolean {
    if (this.deps.now() - this.lastSeekAt > SPURIOUS_END_WINDOW_MS) return false
    const dur = this.active.el.duration
    if (!Number.isFinite(dur) || dur <= 0) return false
    return this.lastSeekTarget < dur - 2
  }

  // An element-level failure (bad range/416/decode stall after a far seek
  // on a huge file): re-seek the last good position and resume if it was
  // playing. Bounded — a genuinely broken file pauses instead of spinning.
  private handleSlotError(slot: Slot) {
    const wasPlaying = !slot.el.paused
    if (slot.recoveries >= MAX_RECOVERIES) {
      this.report(`error recovery exhausted, pausing ${this.describeSlot(slot)}`)
      try {
        slot.el.pause()
      } catch {
        /* ignore */
      }
      return
    }
    slot.recoveries += 1
    this.report(
      `element error #${slot.recoveries}, recovering at lastGood=${this.lastGoodTime} ${this.describeSlot(slot)}`,
    )
    try {
      slot.el.currentTime = Math.max(0, this.lastGoodTime)
      this.lastSeekAt = this.deps.now()
      this.lastSeekTarget = Math.max(0, this.lastGoodTime)
      this.lastAdvanceAt = this.deps.now()
      if (wasPlaying) this.tryPlay(slot.el, 'error-recovery')
    } catch (e: unknown) {
      this.report(
        `error recovery threw: ${e instanceof Error ? e.message : String(e)} ${this.describeSlot(slot)}`,
      )
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

  // Re-attach an unloaded stream at the last good position (or the seek
  // target if newer), resuming only if it was playing. Bounded like all
  // recoveries; exhaustion pauses instead of fake-playing.
  private recoverUnload(slot: Slot) {
    const wasPlaying = !slot.el.paused
    if (slot.recoveries >= MAX_RECOVERIES) {
      this.report(`unload recovery exhausted, pausing ${this.describeSlot(slot)}`)
      try {
        slot.el.pause()
      } catch {
        /* ignore */
      }
      return
    }
    slot.recoveries += 1
    this.report(`stream unloaded, re-attaching (#${slot.recoveries}) ${this.describeSlot(slot)}`)
    try {
      // Re-assigning src forces the pipeline to fetch again; without this
      // the element sits EMPTY forever even though the URL is fine.
      if (slot.url) slot.el.src = slot.url
      slot.el.currentTime = Math.max(0, this.lastGoodTime)
      this.lastAdvanceAt = this.deps.now()
      if (wasPlaying) this.tryPlay(slot.el, 'unload-recovery')
    } catch (e: unknown) {
      this.report(`unload recovery threw: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  // Frozen-clock watchdog: a playing element whose time stops advancing
  // (stalled range request on a huge file, no error event) gets a recovery
  // seek; the error listener covers hard failures. Seeking state never
  // counts as stalled — the clock legitimately holds still mid-seek.
  private watchStall() {
    const el = this.active.el
    const t = el.currentTime
    const now = this.deps.now()
    if (el.seeking || !Number.isFinite(t) || t < 0) return
    if (t > this.lastGoodTime) {
      this.lastGoodTime = t
      this.lastAdvanceAt = now
      return
    }
    if (now - this.lastAdvanceAt > STALL_TIMEOUT_MS) {
      const frozenSec = Math.round((now - this.lastAdvanceAt) / 1000)
      this.lastAdvanceAt = now
      this.report(`stall watchdog fired (frozen ~${frozenSec}s): ${this.describeSlot(this.active)}`)
      this.handleSlotError(this.active)
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

  private tick() {
    // Fully idle (nothing loaded, no fade): park the ticker instead of
    // spinning every 250ms for the lifetime of the page. play()/load()
    // restart it on demand.
    if (!this.fading && !this.active.file) {
      this.stopTimer()
      return
    }
    // Unloaded stream (the suspected long-file killer): EMPTY/NO_SOURCE
    // with a file still assigned, past the post-load/seek grace. Fires
    // whether paused or not — a user-paused element keeps its resource, so
    // this only trips on genuine loss. Recovery re-attaches the stream.
    if (this.active.file !== null && this.isUnloaded(this.active)) {
      const now = this.deps.now()
      if (now - this.lastLoadAt > UNLOAD_GRACE_MS && now - this.lastSeekAt > UNLOAD_GRACE_MS) {
        this.lastLoadAt = now
        this.recoverUnload(this.active)
        return
      }
    }
    if (!this.active.el || this.active.el.paused) {
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
      }
      if (remaining > this.fadeSec) return
    }
    if (remaining <= this.fadeSec) void this.startFade(next)
  }

  private async startFade(next: EngineTrack) {
    const idle = this.idle
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
  private abortTo(actual: EngineTrack | null) {
    if (!this.fading) return
    const incoming = this.fading.incoming
    incoming.el.pause()
    this.setSlotGain(incoming, 0)
    this.fading = null
    if (actual && this.active.file !== actual.file) {
      this.active.el.src = actual.url
      this.active.el.currentTime = 0
      this.active.file = actual.file
      this.active.url = actual.url
      this.active.isPodcast = actual.isPodcast
      this.active.baseGain = gainForDb(actual.gainDb)
      this.active.recoveries = 0
      this.lastLoadAt = this.deps.now()
      this.setSlotGain(this.active, 1)
      this.tryPlay(this.active.el, 'abortTo')
    } else {
      this.setSlotGain(this.active, 1)
    }
  }
}

// App-wide singleton (the store and components share it).
export const engine = new AudioEngine()
