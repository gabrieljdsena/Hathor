import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AudioEngine, fadeGains, type AudioContextLike, type AudioElementLike, type EngineTrack } from './engine'

class FakeEl implements AudioElementLike {
  src = ''
  currentTime = 0
  duration = Number.NaN
  paused = true
  volume = 1
  plays = 0
  private ended: Array<() => void> = []

  play() {
    this.plays += 1
    this.paused = false
    return Promise.resolve()
  }

  pause() {
    this.paused = true
  }

  addEventListener(_type: 'ended', fn: () => void) {
    this.ended.push(fn)
  }

  fireEnded() {
    for (const fn of this.ended) fn()
  }
}

class FakeGain {
  gain = { value: 0 }
  connect() {}
}

class FakeCtx implements AudioContextLike {
  destination = {}
  state = 'running'
  gains: FakeGain[] = []
  resumed = 0

  createGain() {
    const g = new FakeGain()
    this.gains.push(g)
    return g
  }

  createMediaElementSource() {
    return { connect() {} }
  }

  resume() {
    this.resumed += 1
    return Promise.resolve()
  }
}

const track = (file: string, podcast = false): EngineTrack => ({
  file,
  url: `http://x/${file}`,
  isPodcast: podcast,
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
