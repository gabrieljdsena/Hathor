import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  AudioEngine,
  END_EPSILON_SEC,
  clampSeekTarget,
  fadeGains,
  type AudioContextLike,
  type AudioElementLike,
  type EngineTrack,
} from './engine'

class FakeEl implements AudioElementLike {
  src = ''
  currentTime = 0
  duration = Number.NaN
  paused = true
  seeking = false
  readyState = 4
  networkState = 2
  error: { readonly code: number; readonly message: string } | null = null
  volume = 1
  plays = 0
  pauses = 0
  reports: string[] = []
  private ended: Array<() => void> = []
  private errored: Array<() => void> = []

  play() {
    this.plays += 1
    this.paused = false
    return Promise.resolve()
  }

  pause() {
    this.pauses += 1
    this.paused = true
  }

  load() {
    this.currentTime = 0
  }

  addEventListener(type: 'ended' | 'error', fn: () => void) {
    if (type === 'error') this.errored.push(fn)
    else this.ended.push(fn)
  }

  fireEnded() {
    for (const fn of this.ended) fn()
  }

  fireError() {
    for (const fn of this.errored) fn()
  }
}

class FakeGain {
  gain = { value: 0 }
  connectedTo: unknown[] = []
  disconnects = 0
  connect(node: unknown) {
    this.connectedTo.push(node)
  }
  disconnect() {
    this.disconnects += 1
  }
}

class FakeComp {
  threshold = { value: 0 }
  knee = { value: 0 }
  ratio = { value: 0 }
  attack = { value: 0 }
  release = { value: 0 }
  connectedTo: unknown[] = []
  disconnects = 0
  connect(node: unknown) {
    this.connectedTo.push(node)
  }
  disconnect() {
    this.disconnects += 1
  }
}

class FakeCtx implements AudioContextLike {
  destination = {}
  state = 'running'
  gains: FakeGain[] = []
  comps: FakeComp[] = []
  resumed = 0

  createGain() {
    const g = new FakeGain()
    this.gains.push(g)
    return g
  }

  createDynamicsCompressor() {
    const c = new FakeComp()
    this.comps.push(c)
    return c
  }

  createMediaElementSource() {
    return { connect() {} }
  }

  resume() {
    this.resumed += 1
    return Promise.resolve()
  }
}

const track = (file: string, podcast = false, gainDb: number | null = null): EngineTrack => ({
  file,
  url: `http://x/${file}`,
  isPodcast: podcast,
  gainDb,
})

function setup() {
  vi.useFakeTimers()
  let now = 100000
  const els: FakeEl[] = []
  const ctx = new FakeCtx()
  const engine = new AudioEngine({
    createElement: () => {
      const el = new FakeEl()
      els.push(el)
      return el
    },
    createContext: () => ctx,
    now: () => now,
  })
  return {
    engine,
    els,
    ctx,
    advance: async (ms: number) => {
      now += ms
      await vi.advanceTimersByTimeAsync(ms)
    },
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('fadeGains (equal-power)', () => {
  it('starts full-out / silent-in and ends silent-out / full-in', () => {
    const [o0, i0] = fadeGains(0)
    expect(o0).toBeCloseTo(1, 5)
    expect(i0).toBeCloseTo(0, 5)
    const [o1, i1] = fadeGains(1)
    expect(o1).toBeCloseTo(0, 5)
    expect(i1).toBeCloseTo(1, 5)
  })

  it('holds -3 dB each at the midpoint (constant power, no dip)', () => {
    const [out, inp] = fadeGains(0.5)
    expect(out).toBeCloseTo(Math.SQRT1_2, 5)
    expect(inp).toBeCloseTo(Math.SQRT1_2, 5)
    expect(out * out + inp * inp).toBeCloseTo(1, 5)
  })

  it('is monotonic', () => {
    let prevOut = 2
    let prevIn = -1
    for (let t = 0; t <= 1.001; t += 0.05) {
      const [out, inp] = fadeGains(t)
      expect(out).toBeLessThanOrEqual(prevOut)
      expect(inp).toBeGreaterThanOrEqual(prevIn)
      prevOut = out
      prevIn = inp
    }
  })
})

describe('AudioEngine transport', () => {
  it('loads and plays a track', async () => {
    const { engine, els } = setup()
    engine.load(track('a.mp3'), true)
    await vi.advanceTimersByTimeAsync(0)
    expect(els[0].src).toBe('http://x/a.mp3')
    expect(els[0].paused).toBe(false)
    expect(engine.currentFile()).toBe('a.mp3')
  })

  it('pauses and resumes', () => {
    const { engine, els } = setup()
    engine.load(track('a.mp3'), true)
    engine.pause()
    expect(els[0].paused).toBe(true)
    engine.play()
    expect(els[0].paused).toBe(false)
  })

  it('routes volume through the master gain', () => {
    const { engine, ctx } = setup()
    engine.load(track('a.mp3'), true)
    engine.setVolume(0.3)
    const master = ctx.gains[0]
    expect(master.gain.value).toBeCloseTo(0.3, 5)
  })

  it('fires onEnded when the active track ends without fading', async () => {
    const { engine, els } = setup()
    const ended: string[] = []
    engine.onEnded(() => ended.push('ended'))
    engine.load(track('a.mp3'), true)
    els[0].fireEnded()
    await vi.advanceTimersByTimeAsync(0)
    expect(ended).toEqual(['ended'])
  })
})

describe('AudioEngine crossfade', () => {
  beforeEach(() => {})

  function playingFiveMinuteTrack() {
    const s = setup()
    s.engine.setCrossfade(true, 5)
    s.engine.load(track('a.mp3'), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 196 // 4s left: inside the 5s fade window
    return s
  }

  it('starts fading when remaining time hits the setting', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250)
    expect(s.engine.isFading()).toBe(true)
    expect(s.els[1].src).toBe('http://x/b.mp3')
    expect(s.els[1].paused).toBe(false)
  })

  it('moves gains along the equal-power curve and swaps at the end', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250) // fade starts
    await s.advance(2500) // halfway through the 5s fade
    const outGain = s.ctx.gains[1].gain.value
    const inGain = s.ctx.gains[2].gain.value
    expect(outGain).toBeLessThan(1)
    expect(inGain).toBeGreaterThan(0)
    expect(outGain * outGain + inGain * inGain).toBeCloseTo(1, 2)
    await s.advance(5000)
    expect(s.engine.isFading()).toBe(false)
    expect(s.engine.currentFile()).toBe('b.mp3')
    expect(s.els[0].paused).toBe(true) // outgoing parked
  })

  it('asks the store to advance exactly at fade start', async () => {
    const s = playingFiveMinuteTrack()
    const begun: string[] = []
    s.engine.setNextProvider(() => track('b.mp3'), async (t) => {
      begun.push(t.file)
      return t
    })
    await s.advance(250)
    expect(begun).toEqual(['b.mp3'])
  })

  it('does not fade when disabled, for podcasts, or without a next track', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setCrossfade(false, 5)
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(1000)
    expect(s.engine.isFading()).toBe(false)

    const p = setup()
    p.engine.setCrossfade(true, 5)
    p.engine.load(track('ep.mp3', true), true)
    p.els[0].duration = 200
    p.els[0].currentTime = 198
    p.engine.setNextProvider(() => track('ep2.mp3', true), async () => null)
    await p.advance(1000)
    expect(p.engine.isFading()).toBe(false)

    const n = setup()
    n.engine.setCrossfade(true, 5)
    n.engine.load(track('a.mp3'), true)
    n.els[0].duration = 200
    n.els[0].currentTime = 198
    n.engine.setNextProvider(() => null, async () => null)
    await n.advance(1000)
    expect(n.engine.isFading()).toBe(false)
  })

  it('freezes fade progress while paused', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250)
    await s.advance(2000)
    const mid = s.ctx.gains[2].gain.value
    s.engine.pause()
    await s.advance(3000)
    expect(s.ctx.gains[2].gain.value).toBeCloseTo(mid, 5)
    expect(s.engine.isFading()).toBe(true)
    s.engine.play()
    await s.advance(4000)
    expect(s.engine.isFading()).toBe(false)
  })

  it('seek cancels the fade onto the displayed track', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250)
    expect(s.engine.isFading()).toBe(true)
    s.engine.seek(42)
    expect(s.engine.isFading()).toBe(false)
    expect(s.engine.currentFile()).toBe('b.mp3')
    expect(s.els[1].currentTime).toBe(42)
  })

  it('finishes early when the outgoing track ends mid-fade', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250)
    expect(s.engine.isFading()).toBe(true)
    s.els[0].fireEnded()
    expect(s.engine.isFading()).toBe(false)
    expect(s.engine.currentFile()).toBe('b.mp3')
  })

  it('exposes the fade target while fading', async () => {
    const s = playingFiveMinuteTrack()
    expect(s.engine.fadeTarget()).toBeNull()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250)
    expect(s.engine.fadeTarget()).toBe('b.mp3')
    await s.advance(6000)
    expect(s.engine.fadeTarget()).toBeNull()
  })

  it('preloads the next track before the fade window', async () => {
    const s = setup()
    s.engine.setCrossfade(true, 5)
    s.engine.load(track('a.mp3'), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 188 // 12s left: inside preload window, outside fade
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(300)
    expect(s.engine.isFading()).toBe(false)
    expect(s.els[1].src).toBe('http://x/b.mp3')
    expect(s.els[1].paused).toBe(true) // staged, not playing yet
  })

  it('falls back to plain playback without Web Audio', async () => {
    vi.useRealTimers()
    let now = 0
    const els: FakeEl[] = []
    const engine = new AudioEngine({
      createElement: () => {
        const el = new FakeEl()
        els.push(el)
        return el
      },
      createContext: () => {
        throw new Error('no webaudio')
      },
      now: () => now,
    })
    engine.load(track('a.mp3'), true)
    expect(els[0].paused).toBe(false)
    expect(engine.time()).toBe(0)
    engine.setCrossfade(true, 5)
    engine.setVolume(0.4)
    expect(els[0].volume).toBeCloseTo(0.4, 5)
  })
})

describe('AudioEngine normalize (leveling)', () => {
  it('routes master straight to destination when off (default)', () => {
    const s = setup()
    s.engine.load(track('a.mp3'), true)
    expect(s.engine.isNormalize()).toBe(false)
    expect(s.ctx.comps).toHaveLength(0)
    expect(s.ctx.gains[0].connectedTo).toContain(s.ctx.destination)
  })

  it('inserts the compressor with music-tuned settings when enabled', () => {
    const s = setup()
    s.engine.setNormalize(true)
    s.engine.load(track('a.mp3'), true)
    expect(s.ctx.comps).toHaveLength(1)
    const comp = s.ctx.comps[0]
    expect(comp.threshold.value).toBe(-18)
    expect(comp.knee.value).toBe(20)
    expect(comp.ratio.value).toBe(3)
    expect(comp.attack.value).toBeCloseTo(0.003, 5)
    expect(comp.release.value).toBe(0.25)
    expect(s.ctx.gains[0].connectedTo).toContain(comp)
    expect(comp.connectedTo).toContain(s.ctx.destination)
  })

  it('rewires live on toggle without touching slot gains', () => {
    const s = setup()
    s.engine.load(track('a.mp3'), true)
    const slotBefore = s.ctx.gains[1].gain.value
    s.engine.setNormalize(true)
    expect(s.ctx.comps).toHaveLength(1)
    s.engine.setNormalize(false)
    const masterWires = s.ctx.gains[0].connectedTo
    expect(masterWires[masterWires.length - 1]).toBe(s.ctx.destination)
    expect(s.ctx.gains[1].gain.value).toBe(slotBefore)
  })

  it('is a stored no-op without Web Audio', () => {
    vi.useRealTimers()
    const engine = new AudioEngine({
      createElement: () => new FakeEl(),
      createContext: () => {
        throw new Error('no webaudio')
      },
      now: () => 0,
    })
    expect(() => engine.setNormalize(true)).not.toThrow()
    engine.load(track('a.mp3'), true)
    expect(engine.isNormalize()).toBe(true)
  })
})

describe('AudioEngine per-track loudness gain', () => {
  it('applies measured gain multiplied with fade gain', () => {
    const s = setup()
    // +8 dB correction: gain = 10^(8/20), applied at unity fade gain.
    s.engine.load(track('hot.mp3', false, 8), true)
    expect(s.ctx.gains[1].gain.value).toBeCloseTo(Math.pow(10, 8 / 20), 5)
  })

  it('uses unity gain without measurement', () => {
    const s = setup()
    s.engine.load(track('a.mp3', false, null), true)
    expect(s.ctx.gains[1].gain.value).toBeCloseTo(1, 5)
  })

  it('clamps extreme corrections in bypass mode', () => {
    vi.useRealTimers()
    const els: FakeEl[] = []
    const engine = new AudioEngine({
      createElement: () => {
        const el = new FakeEl()
        els.push(el)
        return el
      },
      createContext: () => {
        throw new Error('no webaudio')
      },
      now: () => 0,
    })
    engine.setVolume(0.9)
    // +30 dB clamps to +12 dB (~4x): 0.9 * 4 exceeds 1, clamps to 1.
    engine.load(track('hot.mp3', false, 30), true)
    expect(els[0].volume).toBeCloseTo(1, 5)
  })

  it('gainForDb mirrors the backend constants', async () => {
    const { gainForDb, LOUDNESS_TARGET_LUFS, LOUDNESS_MAX_CORRECTION_DB } =
      await import('./engine')
    expect(LOUDNESS_TARGET_LUFS).toBe(-14)
    expect(LOUDNESS_MAX_CORRECTION_DB).toBe(12)
    expect(gainForDb(null)).toBe(1)
    expect(gainForDb(Number.NaN)).toBe(1)
    expect(gainForDb(0)).toBe(1)
    expect(gainForDb(-8)).toBeCloseTo(Math.pow(10, -8 / 20), 5)
    expect(gainForDb(30)).toBeCloseTo(Math.pow(10, 12 / 20), 5)
    expect(gainForDb(-30)).toBeCloseTo(Math.pow(10, -12 / 20), 5)
  })
})

describe('clampSeekTarget', () => {
  it('stops short of a known duration', () => {
    expect(clampSeekTarget(20000, 10800)).toBeCloseTo(10800 - END_EPSILON_SEC, 5)
    expect(clampSeekTarget(100, 200)).toBe(100)
    expect(clampSeekTarget(-5, 200)).toBe(0)
  })

  it('passes through when duration is unknown', () => {
    expect(clampSeekTarget(5000, Number.NaN)).toBe(5000)
    expect(clampSeekTarget(5000, 0)).toBe(5000)
    expect(clampSeekTarget(Number.NaN, 200)).toBe(0)
  })
})

describe('AudioEngine long-track seeks (hours-long podcasts)', () => {
  function playingThreeHourTrack() {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    s.els[0].currentTime = 3600
    return s
  }

  it('clamps far seeks short of the duration edge', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    s.engine.seek(20000)
    expect(s.els[0].currentTime).toBeCloseTo(10800 - END_EPSILON_SEC, 5)
  })

  it('treats ended right after an inside-range seek as spurious', async () => {
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.engine.seek(5000)
    await s.advance(200)
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    expect(s.els[0].currentTime).toBe(5000)
    expect(s.engine.currentFile()).toBe('ep.mp3')
  })

  it('still advances on a genuine finish after a seek', async () => {
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.engine.seek(5000)
    await s.advance(2000) // past the spurious-end window
    s.els[0].fireEnded()
    expect(ended).toEqual(['ended'])
  })

  it('recovers a failed element at the last good position', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    s.els[0].currentTime = 5000
    await s.advance(250) // tick records lastGoodTime = 5000
    const plays = s.els[0].plays
    s.els[0].currentTime = 9999 // glitch state before the error surfaces
    s.els[0].fireError()
    expect(s.els[0].currentTime).toBe(5000)
    expect(s.els[0].plays).toBeGreaterThan(plays)
    expect(s.els[0].paused).toBe(false)
  })

  it('gives up (pauses) after repeated failures instead of spinning', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    for (let i = 0; i < 3; i += 1) s.els[0].fireError()
    expect(s.els[0].paused).toBe(false)
    s.els[0].fireError()
    expect(s.els[0].paused).toBe(true)
  })

  it('rescues a frozen clock via the stall watchdog', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    const plays = s.els[0].plays
    // Clock frozen (buffering stall, no error event): 11s without advance.
    await s.advance(11000)
    expect(s.els[0].plays).toBeGreaterThan(plays)
    expect(s.els[0].paused).toBe(false)
  })

  it('does not mistake a fresh load for a stall', async () => {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    // Frozen from the start, but the watchdog grace starts at load.
    await s.advance(5000)
    expect(s.els[0].paused).toBe(false)
  })

  it('time() and duration() survive throwing reads', async () => {
    const s = setup()
    const reports: string[] = []
    s.engine.onReport = (m) => reports.push(m)
    s.engine.load(track('ep.mp3', true), true)
    Object.defineProperty(s.els[0], 'currentTime', {
      configurable: true,
      get: () => {
        throw new Error('unloaded')
      },
    })
    expect(s.engine.time()).toBe(0)
    expect(reports.some((m) => m.includes('time() read threw'))).toBe(true)
  })

  it('re-attaches an unloaded stream at the last good position', async () => {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    s.els[0].currentTime = 5000
    await s.advance(250) // tick records lastGoodTime = 5000
    // The pipeline drops the stream (the suspected long-file killer).
    s.els[0].networkState = 3
    s.els[0].src = ''
    await s.advance(6000) // past the unload grace
    expect(s.els[0].src).toBe('http://x/ep.mp3')
    expect(s.els[0].currentTime).toBe(5000)
    expect(s.els[0].paused).toBe(false)
  })

  it('ignores transient emptiness right after load', async () => {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].networkState = 0 // EMPTY before first bytes: normal, not loss
    await s.advance(1000)
    expect(s.els[0].src).toBe('http://x/ep.mp3') // untouched
    expect(s.els[0].paused).toBe(false)
  })

  it('gives up re-attaching after repeated loss', async () => {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    await s.advance(250)
    for (let i = 0; i < 4; i += 1) {
      s.els[0].networkState = 3
      await s.advance(6000)
    }
    expect(s.els[0].paused).toBe(true)
  })
  it('reports element errors with media details for debugging', async () => {
    const s = setup()
    const reports: string[] = []
    s.engine.onReport = (m) => reports.push(m)
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    await s.advance(250)
    s.els[0].error = { code: 4, message: 'src not supported' }
    s.els[0].fireError()
    expect(reports.some((m) => m.includes('element error'))).toBe(true)
    expect(reports.some((m) => m.includes('4:src not supported'))).toBe(true)
  })

  it('reports spurious ends for debugging', async () => {
    const s = setup()
    const reports: string[] = []
    s.engine.onReport = (m) => reports.push(m)
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    await s.advance(250)
    s.engine.seek(7000)
    await s.advance(200)
    s.els[0].fireEnded()
    expect(reports.some((m) => m.includes('spurious ended ignored'))).toBe(true)
  })
})
