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

  it('restarts the same file only when explicitly asked', () => {
    const { engine, els } = setup()
    engine.load(track('a.mp3'), true)
    els[0].currentTime = 42

    engine.load(track('a.mp3'), true)
    expect(els[0].currentTime).toBe(42)

    engine.load(track('a.mp3'), true, true)
    expect(els[0].currentTime).toBe(0)
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

  it('hands the outgoing file to the advance callback', async () => {
    const s = playingFiveMinuteTrack()
    const seen: Array<{ expected: string; out: string | null }> = []
    s.engine.setNextProvider(
      () => track('b.mp3'),
      async (t, outFile) => {
        seen.push({ expected: t.file, out: outFile })
        return t
      },
    )
    await s.advance(250)
    expect(seen).toEqual([{ expected: 'b.mp3', out: 'a.mp3' }])
  })

  it('pauses both slots when pausing mid-fade', async () => {
    const s = playingFiveMinuteTrack()
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    await s.advance(250) // fade starts, both slots audible
    expect(s.els[0].paused).toBe(false)
    expect(s.els[1].paused).toBe(false)
    s.engine.pause()
    expect(s.els[0].paused).toBe(true)
    expect(s.els[1].paused).toBe(true)
    expect(s.engine.isPlaying()).toBe(false)
    expect(s.engine.isFading()).toBe(true) // frozen, resumable
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
    s.els[0].currentTime = 10799 // at the real end, not mid-track
    s.els[0].fireEnded()
    expect(ended).toEqual(['ended'])
  })

  it('recovers instead of advancing on a cut-short ended', async () => {
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250) // tick records lastGoodTime = 3600
    s.els[0].currentTime = 47 // stream cut out mid-track
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    // Recovery re-attaches on the retry timer, not synchronously.
    await s.advance(500)
    expect(s.els[0].currentTime).toBe(3600)
    expect(s.els[0].paused).toBe(false)
    expect(s.engine.currentFile()).toBe('ep.mp3')
  })

  it('pauses instead of spinning on a deterministically cut file', async () => {
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    for (let i = 0; i < 3; i += 1) {
      s.els[0].currentTime = 47
      s.els[0].fireEnded()
      expect(s.els[0].paused).toBe(false)
    }
    s.els[0].currentTime = 47
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    expect(s.els[0].paused).toBe(true)
  })

  it('grants one spurious recovery per seek, then treats repeats as cut-short', async () => {
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.engine.seek(5000)
    await s.advance(200)
    s.els[0].fireEnded() // spurious: re-seeks the target
    expect(ended).toEqual([])
    expect(s.els[0].currentTime).toBe(5000)
    // Target itself is past a cut point: ends again at once. Must not
    // re-seek forever — cut-short recovery instead of advancing.
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    expect(s.engine.currentFile()).toBe('ep.mp3')
  })

  it('ignores ended on a paused element instead of advancing', async () => {
    // A paused element at the edge got there by seek (drift correction
    // past the real end, chapter past EOF), not by playing through:
    // advancing would skip the user's episode over nothing.
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.engine.pause()
    s.els[0].currentTime = 10799.9 // correction overshoot onto the edge
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    expect(s.engine.currentFile()).toBe('ep.mp3')
    expect(s.els[0].paused).toBe(true)
    // Parked just inside so resume plays the tail instead of sitting on
    // the edge (or skipping away).
    expect(s.els[0].currentTime).toBeCloseTo(10800 - END_EPSILON_SEC, 5)
  })

  it('leaves a mid-track paused ended where it is', async () => {
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.engine.pause()
    s.els[0].currentTime = 3600
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    expect(s.els[0].currentTime).toBe(3600)
    expect(s.els[0].paused).toBe(true)
  })

  it('stays paused on ended right after seek+pause (no spurious resurrect)', async () => {
    // Regression: the spurious-end branch used to run before the paused
    // check, so seek-then-pause near EOF resumed the same track.
    const s = playingThreeHourTrack()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.engine.seek(5000)
    s.engine.pause()
    await s.advance(200) // still inside the spurious-end window
    s.els[0].fireEnded()
    expect(ended).toEqual([])
    expect(s.els[0].paused).toBe(true)
    expect(s.engine.currentFile()).toBe('ep.mp3')
  })

  it('keeps the legacy advance for tiny or unknown durations', async () => {
    const s = setup()
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    s.engine.load(track('clip.mp3'), true)
    await s.advance(250)
    // Unknown duration (metadata not loaded): advance as before.
    s.els[0].fireEnded()
    expect(ended).toEqual(['ended'])
    // Tiny duration (jingle/clip): advance as before, never recover.
    s.els[0].duration = 4
    s.els[0].currentTime = 1
    s.els[0].fireEnded()
    expect(ended).toEqual(['ended', 'ended'])
  })

  it('reports genuine advances to the server log bridge', async () => {
    const s = playingThreeHourTrack()
    const reports: string[] = []
    s.engine.onReport = (m) => reports.push(m)
    const ended: string[] = []
    s.engine.onEnded(() => ended.push('ended'))
    await s.advance(250)
    s.els[0].currentTime = 10799
    s.els[0].fireEnded()
    expect(ended).toEqual(['ended'])
    expect(reports.some((m) => m.includes('ended, advancing'))).toBe(true)
  })

  it('reports cut-short ends to the server log bridge', async () => {
    const s = playingThreeHourTrack()
    const reports: string[] = []
    s.engine.onReport = (m) => reports.push(m)
    await s.advance(250)
    s.els[0].currentTime = 47
    s.els[0].fireEnded()
    expect(reports.some((m) => m.includes('cut-short ended, recovering'))).toBe(true)
  })

  it('recovers a failed element at the last good position', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    s.els[0].currentTime = 5000
    await s.advance(250) // tick records lastGoodTime = 5000
    const plays = s.els[0].plays
    s.els[0].currentTime = 9999 // glitch state before the error surfaces
    s.els[0].fireError()
    expect(s.els[0].paused).toBe(true) // held during the retry backoff
    await s.advance(500)
    expect(s.els[0].currentTime).toBe(5000)
    expect(s.els[0].plays).toBeGreaterThan(plays)
    expect(s.els[0].paused).toBe(false)
  })

  it('keeps retrying transient failures instead of stranding the song', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    // No three-strike cap: a flaky connection must keep trying while the
    // store still says "playing" (the old cap left a silent dead player).
    for (let i = 0; i < 5; i += 1) {
      s.els[0].fireError()
      expect(s.els[0].paused).toBe(true)
      await s.advance(500 * 2 ** Math.min(i, 4))
      expect(s.els[0].paused).toBe(false)
    }
    expect(s.els[0].plays).toBeGreaterThan(5)
    expect(s.engine.currentFile()).toBe('ep.mp3')
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

  it('keeps re-attaching a repeatedly lost stream with fresh URLs', async () => {
    const s = setup()
    const fetched: string[] = []
    s.engine.setStreamUrlProvider((file) => {
      fetched.push(file)
      return `http://x/${file}?v=${fetched.length}`
    })
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    await s.advance(250)
    for (let i = 0; i < 4; i += 1) {
      s.els[0].networkState = 3
      await s.advance(6000)
      expect(s.els[0].paused).toBe(false)
    }
    expect(fetched.length).toBeGreaterThanOrEqual(4)
    expect(s.els[0].src).toContain('?v=')
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
    expect(reports.some((m) => m.includes('element fatal error'))).toBe(true)
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

describe('AudioEngine driftTarget (server correction caps)', () => {
  function playingThreeHourTrack() {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    s.els[0].currentTime = 3600
    return s
  }

  it('returns null when element and server agree', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250) // tick records lastGoodTime = 3600
    expect(s.engine.driftTarget(3600.5, 10800)).toBeNull()
    expect(s.engine.driftTarget(3601.9, 10800)).toBeNull()
  })

  it('returns null for garbage server positions', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    expect(s.engine.driftTarget(Number.NaN, 10800)).toBeNull()
    expect(s.engine.driftTarget(-5, 10800)).toBeNull()
  })

  it('caps a runaway estimate short of the duration edge', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    // No recovery yet: plain edge-capped forward jump (refresh-resume path).
    expect(s.engine.driftTarget(10799.9, 10800)).toBeCloseTo(10800 - 2, 5)
  })

  it('allows forward jumps before any recovery (refresh resume)', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    // 3600 -> 5000 is a legitimate remote seek: no failure recorded yet.
    expect(s.engine.driftTarget(5000, 10800)).toBe(5000)
  })

  it('caps forward drift at the observed-playable ceiling after a recovery', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250) // lastGoodTime = 3600
    // Slider seek far forward into an unplayable range, then an element
    // error: the engine falls back to the last good position.
    s.engine.seek(8000)
    await s.advance(10)
    s.els[0].currentTime = 9999 // glitch state before the error surfaces
    s.els[0].fireError()
    await s.advance(500) // retry timer re-attaches at the last good spot
    expect(s.els[0].currentTime).toBe(3600)
    expect(s.els[0].paused).toBe(false)
    // The server still names the poisoned target — drift must NOT jump
    // back into it (that loop exhausts the budget and freezes the bar
    // with a dead toggle). Parked at the ceiling: no correction churn.
    expect(s.engine.driftTarget(8002, 10800)).toBeNull()
    // Stranded between the ceiling and the poison (an earlier system
    // correction): rescued to the ceiling, never re-poisoned.
    s.engine.seek(5000, false)
    expect(s.engine.driftTarget(8000, 10800)).toBe(3600)
  })

  it('lets a fresh user seek retry the spot (intent wins)', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    s.engine.seek(8000)
    await s.advance(10)
    s.els[0].currentTime = 9999
    s.els[0].fireError()
    await s.advance(500)
    expect(s.els[0].currentTime).toBe(3600)
    // The user deliberately retries the same region: the ceiling lifts for
    // the new attempt. If it is truly unplayable it will stall, recover,
    // and the ceiling re-engages on its own.
    await s.advance(10)
    s.engine.seek(8000)
    expect(s.engine.driftTarget(8000, 10800)).toBeNull() // element holds the spot: nothing to do
    s.engine.seek(5000, false)
    await s.advance(10)
    s.engine.seek(8000)
    expect(s.engine.driftTarget(8100, 10800)).toBe(8100) // server further ahead: normal correction
  })

  it('tracks the grown high-water mark for later failures', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250) // lastGoodTime = 3600
    s.els[0].currentTime = 3700
    await s.advance(250) // lastGoodTime = 3700
    s.engine.seek(9000)
    await s.advance(10)
    s.els[0].currentTime = 9999
    s.els[0].fireError() // falls back to 3700, not the stale 3600
    await s.advance(500)
    expect(s.els[0].currentTime).toBe(3700)
    expect(s.engine.driftTarget(9000, 10800)).toBeNull() // parked at the grown ceiling
    s.engine.seek(4000, false) // stranded mid-way by a system correction
    expect(s.engine.driftTarget(9000, 10800)).toBe(3700) // rescued to the ceiling, never 9000
  })

  it('survives repeated drift attempts without exhausting the budget', async () => {
    // The freeze loop: poisoned server position plus a drift correction on
    // every server round trip while the element plays on healthily from
    // the ceiling. Corrections must stay pinned (null) instead of jumping
    // into the poison and burning the recovery budget.
    const s = playingThreeHourTrack()
    await s.advance(250)
    s.engine.seek(8000)
    await s.advance(10)
    s.els[0].currentTime = 9999
    s.els[0].fireError() // recovery #1 -> back at 3600, still playing
    await s.advance(500)
    expect(s.els[0].paused).toBe(false)
    for (let i = 1; i <= 10; i += 1) {
      s.els[0].currentTime = 3600 + i // healthy advance from the ceiling
      await s.advance(250)
      expect(s.engine.driftTarget(8000, 10800)).toBeNull()
      expect(s.els[0].paused).toBe(false)
    }
    // A later genuine failure still recovers instead of finding an
    // exhausted counter and pausing mid-episode.
    s.els[0].fireError()
    await s.advance(1000) // retry #2 backoff
    expect(s.els[0].paused).toBe(false)
    expect(s.engine.currentFile()).toBe('ep.mp3')
  })

  it('resets the ceiling on track change', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250)
    s.engine.seek(8000)
    s.els[0].currentTime = 9999
    s.els[0].fireError()
    await s.advance(500)
    // Parked at the ceiling: suppressed to a no-op, never 8000.
    expect(s.engine.driftTarget(8000, 10800)).toBeNull()
    s.engine.load(track('next.mp3', true), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 5
    await s.advance(250)
    // Fresh file, no failures: forward corrections behave normally again.
    expect(s.engine.driftTarget(100, 200)).toBe(100)
  })
})

describe('AudioEngine tail-stuck advance (unfinishable final bytes)', () => {
  function playingNearEnd() {
    const s = setup()
    s.engine.load(track('s.mp3'), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 197
    return s
  }

  it('advances instead of pausing when the budget exhausts at the tail', async () => {
    const s = playingNearEnd()
    const advanced: string[] = []
    s.engine.onTailStuck(() => advanced.push('tail'))
    await s.advance(250) // tick records lastGoodTime = 197
    // Three bounded retries, then the strand is treated as finished.
    for (let i = 0; i < 3; i += 1) {
      s.els[0].fireError()
      await s.advance(500 * 2 ** i)
      expect(s.els[0].paused).toBe(false)
    }
    s.els[0].fireError() // exhausted at 197/200: finished, not broken
    expect(advanced).toEqual(['tail'])
    expect(s.els[0].paused).toBe(true)
  })

  it('keeps retrying mid-track failures instead of pausing', async () => {
    const s = setup()
    const advanced: string[] = []
    s.engine.onTailStuck(() => advanced.push('tail'))
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    s.els[0].currentTime = 3600
    await s.advance(250)
    // Mid-track network failures are never treated as a finished track:
    // unbounded retries keep real content from being abandoned.
    for (let i = 0; i < 4; i += 1) {
      s.els[0].fireError()
      await s.advance(500 * 2 ** Math.min(i, 3))
      expect(s.els[0].paused).toBe(false)
    }
    expect(advanced).toEqual([])
  })

  it('never advances a deliberately paused strand', async () => {
    const s = playingNearEnd()
    const advanced: string[] = []
    s.engine.onTailStuck(() => advanced.push('tail'))
    await s.advance(250)
    s.engine.pause()
    for (let i = 0; i < 4; i += 1) s.els[0].fireError()
    expect(advanced).toEqual([])
    expect(s.els[0].paused).toBe(true)
  })

  it('fires only once per cooldown (a failed advance must not double-skip)', async () => {
    const s = playingNearEnd()
    const advanced: string[] = []
    s.engine.onTailStuck(() => advanced.push('tail'))
    await s.advance(250)
    for (let i = 0; i < 3; i += 1) {
      s.els[0].fireError()
      await s.advance(500 * 2 ** i)
    }
    s.els[0].fireError()
    expect(advanced).toEqual(['tail'])
    // Toggle-play retry inside the cooldown: parks, does not advance again.
    s.engine.play()
    s.els[0].fireError()
    expect(advanced).toEqual(['tail'])
    expect(s.els[0].paused).toBe(true)
    // Past the cooldown a renewed strand advances again.
    await s.advance(31000)
    s.engine.play()
    s.els[0].fireError()
    expect(advanced).toEqual(['tail', 'tail'])
  })

  it('does not tail-advance unknown or tiny durations', async () => {
    const unknown = setup()
    const advancedUnknown: string[] = []
    unknown.engine.onTailStuck(() => advancedUnknown.push('tail'))
    unknown.engine.load(track('s.mp3'), true)
    unknown.els[0].duration = Number.NaN
    await unknown.advance(250)
    unknown.els[0].fireError()
    await unknown.advance(500)
    expect(advancedUnknown).toEqual([])
    expect(unknown.els[0].paused).toBe(false) // still retrying, not "finished"

    const tiny = setup()
    const advancedTiny: string[] = []
    tiny.engine.onTailStuck(() => advancedTiny.push('tail'))
    tiny.engine.load(track('jingle.mp3'), true)
    tiny.els[0].duration = 4
    tiny.els[0].currentTime = 3
    await tiny.advance(250)
    for (let i = 0; i < 4; i += 1) {
      tiny.els[0].fireError()
      await tiny.advance(500 * 2 ** Math.min(i, 3))
    }
    expect(advancedTiny).toEqual([])
    expect(tiny.els[0].paused).toBe(false)
  })

  it('advances on cut-short exhaustion at the tail too', async () => {
    const s = playingNearEnd()
    const advanced: string[] = []
    s.engine.onTailStuck(() => advanced.push('tail'))
    await s.advance(250) // lastGoodTime = 197
    // Stream keeps cutting out just short of the end.
    for (let i = 0; i < 3; i += 1) {
      s.els[0].currentTime = 150
      s.els[0].fireEnded()
      expect(s.els[0].paused).toBe(false)
    }
    s.els[0].currentTime = 150
    s.els[0].fireEnded()
    expect(advanced).toEqual(['tail'])
  })

  it('exposes the observed media time for the instant-death breaker', async () => {
    const s = playingNearEnd()
    expect(s.engine.lastGoodPosition()).toBe(0)
    await s.advance(250)
    expect(s.engine.lastGoodPosition()).toBe(197)
  })
})

describe('AudioEngine fade era tracking', () => {
  function fadingPair() {
    const s = setup()
    s.engine.setCrossfade(true, 5)
    s.engine.load(track('a.mp3'), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 196 // 4s left: inside the 5s fade window
    s.engine.setNextProvider(() => track('b.mp3'), async () => null)
    return s
  }

  it('does not catapult the new song to the old song’s position after a fade', async () => {
    // The reported glitch: a few seconds after every crossfade transition,
    // the new song jumped to nearly the end. The watchdog still described
    // the OUTGOING track (lastGoodTime 196, aging lastAdvanceAt), so it
    // "recovered" the incoming onto the old position.
    const s = fadingPair()
    await s.advance(250) // fade A->B starts
    expect(s.engine.isFading()).toBe(true)
    await s.advance(4000) // fade completes; B renders from ~0
    expect(s.engine.isFading()).toBe(false)
    expect(s.engine.currentFile()).toBe('b.mp3')
    s.els[1].duration = 200
    s.els[1].currentTime = 5 // B plays a few seconds…
    await s.advance(250)
    await s.advance(11000) // …then its clock freezes: watchdog must use B's era
    expect(s.els[1].currentTime).toBe(5)
    expect(s.els[1].paused).toBe(false)
  })

  it('adopts the incoming era when a seek cuts over mid-fade', async () => {
    const s = fadingPair()
    await s.advance(250) // fade A->B starts
    expect(s.engine.isFading()).toBe(true)
    s.engine.seek(42) // keep the displayed (incoming) track at 42
    expect(s.engine.isFading()).toBe(false)
    expect(s.engine.currentFile()).toBe('b.mp3')
    s.els[1].currentTime = 43
    await s.advance(250)
    await s.advance(11000) // frozen clock: recover to 43, never to A's 196
    expect(s.els[1].currentTime).toBe(43)
    expect(s.els[1].paused).toBe(false)
  })

  it('grants a fresh budget to a reused incoming slot', async () => {
    const s = setup()
    s.engine.setCrossfade(true, 5)
    s.engine.load(track('a.mp3'), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 196
    const upcoming = ['b.mp3']
    s.engine.setNextProvider(() => track(upcoming[0]), async () => null)
    await s.advance(250) // fade A->B starts; B renders on els[1]
    // B's stream errors repeatedly mid-fade, dirtying that slot's budget.
    s.els[1].fireError()
    s.els[1].fireError()
    s.els[1].fireError()
    // Manual switch to C mid-fade parks the dirty slot as idle.
    s.engine.load(track('c.mp3'), true)
    s.els[0].duration = 200
    s.els[0].currentTime = 196
    upcoming[0] = 'd.mp3'
    await s.advance(250) // fade C->D reuses the dirty slot for D
    expect(s.engine.isFading()).toBe(true)
    // A transient error on the fresh incoming must recover, not give up
    // with zero retries on arrival.
    s.els[1].fireError()
    expect(s.els[1].paused).toBe(false)
  })
})

describe('AudioEngine seek budget + unload re-attach', () => {
  function playingThreeHourTrack() {
    const s = setup()
    s.engine.load(track('ep.mp3', true), true)
    s.els[0].duration = 10800
    s.els[0].currentTime = 3600
    return s
  }

  it('grants a fresh budget on explicit user seeks', async () => {
    const s = playingThreeHourTrack()
    const fatal: string[] = []
    s.engine.onFatal((file) => fatal.push(file))
    await s.advance(250)
    s.els[0].error = { code: 4, message: 'src not supported' }
    s.els[0].fireError() // recovery #1
    await s.advance(500)
    s.els[0].fireError() // recovery #2
    await s.advance(1000)
    // Dragging the slider back is deliberate intent: failures at the old
    // spot must not instantly exhaust the new one.
    s.engine.seek(100)
    await s.advance(250)
    s.els[0].fireError() // fresh budget #1
    await s.advance(500)
    s.els[0].fireError() // fresh budget #2 — must still play
    await s.advance(1000)
    expect(fatal).toEqual([])
    expect(s.els[0].paused).toBe(false)
  })

  it('does not mint fresh budget on system (drift) seeks', async () => {
    const s = playingThreeHourTrack()
    const fatal: string[] = []
    s.engine.onFatal((file) => fatal.push(file))
    await s.advance(250)
    s.els[0].error = { code: 4, message: 'src not supported' }
    s.els[0].fireError() // deterministic failure recovery #1
    await s.advance(500)
    s.engine.seek(3600, false) // drift-style correction
    s.els[0].fireError() // recovery #2, not #1 again
    await s.advance(1000)
    s.els[0].fireError() // recovery #3
    await s.advance(2000)
    s.els[0].fireError() // exhausted -> hand off instead of spinning
    expect(fatal).toEqual(['ep.mp3'])
    expect(s.els[0].paused).toBe(true)
  })

  it('re-attaches an unloaded stream on element error', async () => {
    const s = playingThreeHourTrack()
    await s.advance(250) // lastGoodTime = 3600
    // Pipeline dropped the stream AND errored (no unload-tick rescue yet).
    s.els[0].networkState = 3
    s.els[0].src = ''
    s.els[0].fireError()
    await s.advance(500)
    expect(s.els[0].src).toBe('http://x/ep.mp3')
    expect(s.els[0].currentTime).toBe(3600)
    expect(s.els[0].paused).toBe(false)
  })
})
