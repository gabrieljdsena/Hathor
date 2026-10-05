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
  volume: number
  play(): Promise<void> | void
  pause(): void
  addEventListener(type: 'ended', fn: () => void): void
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
  isPodcast: boolean
  baseGain: number
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
  private beginNext: (expected: EngineTrack) => Promise<EngineTrack | null> = async () => null
  private endedCb: () => void = () => {}
  private timer: ReturnType<typeof setInterval> | null = null

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
    const mkSlot = (): Slot => ({ el: this.deps.createElement(), gain: null, file: null, isPodcast: false, baseGain: 1 })
    this.a = mkSlot()
    this.b = mkSlot()
    this.active = this.a
    this.a.el.addEventListener('ended', () => this.handleEnded(this.a))
    this.b.el.addEventListener('ended', () => this.handleEnded(this.b))
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
    begin: (expected: EngineTrack) => Promise<EngineTrack | null>,
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
      this.stopTimer()
      return
    }
    if (this.active.file !== track.file) {
      this.active.el.src = track.url
      this.active.el.currentTime = 0
      this.active.file = track.file
      this.active.isPodcast = track.isPodcast
      this.active.baseGain = gainForDb(track.gainDb)
    }
    this.setSlotGain(this.active, 1)
    if (autoplay) safelyPlay(this.active.el)
    else this.active.el.pause()
    this.startTimer()
  }

  play() {
    this.ensureContext()
    this.startTimer()
    // Resuming a paused fade shifts its start so progress freezes cleanly.
    if (this.fading && this.fading.pauseBeganAt !== null) {
      this.fading.start += this.deps.now() - this.fading.pauseBeganAt
      this.fading.pauseBeganAt = null
    }
    safelyPlay(this.active.el)
    if (this.fading) safelyPlay(this.fading.incoming.el)
  }

  pause() {
    if (this.fading && this.fading.pauseBeganAt === null) this.fading.pauseBeganAt = this.deps.now()
    this.active.el.pause()
    if (this.fading) this.fading.incoming.el.pause()
  }

  seek(sec: number) {
    if (this.fading) this.cancelToIncoming()
    try {
      this.active.el.currentTime = Math.max(0, sec)
    } catch {
      /* a failed seek must never break playback */
    }
  }

  // ---- read model (drives progress bars, lyrics, MediaSession) --------

  time(): number {
    const t = this.active.el.currentTime
    return Number.isFinite(t) ? Math.max(0, t) : 0
  }

  duration(): number {
    const d = this.active.el.duration
    return Number.isFinite(d) && d > 0 ? d : 0
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
    if (slot === this.active) this.endedCb()
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
    // estimate and the new track stay aligned. A changed queue (remote
    // edit mid-fade) hard-switches to whatever the server says is current.
    void this.beginNext(next)
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
      this.active.isPodcast = actual.isPodcast
      this.active.baseGain = gainForDb(actual.gainDb)
      this.setSlotGain(this.active, 1)
      safelyPlay(this.active.el)
    } else {
      this.setSlotGain(this.active, 1)
    }
  }
}

function safelyPlay(el: AudioElementLike) {
  try {
    const p = el.play() as Promise<void> | undefined
    if (p && typeof p.catch === 'function') p.catch(() => {})
  } catch {
    /* autoplay policy / missing bytes must never break playback */
  }
}

// App-wide singleton (the store and components share it).
export const engine = new AudioEngine()
