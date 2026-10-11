import { beforeEach, describe, expect, it, vi } from 'vitest'
import { engine } from './audio/engine'
import { usePlayer } from './store/player'
import type { PlayerState } from './api/client'

const { mockFile, mockDur, mockTime, mockPlaying, mockFading, mockToggle } = vi.hoisted(() => ({
  mockFile: vi.fn(),
  mockDur: vi.fn(),
  mockTime: vi.fn(),
  mockPlaying: vi.fn(),
  mockFading: vi.fn(),
  mockToggle: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      toggle: (...args: unknown[]) => mockToggle(...args),
    },
  }
})

vi.mock('./audio/engine', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./audio/engine')>()
  return {
    // Real pure helpers/consts (DRIFT_EDGE_MARGIN_SEC): only the engine
    // singleton is fake.
    ...mod,
    engine: {
      isFading: (...args: unknown[]) => mockFading(...args),
      fadeTarget: () => null,
      currentFile: (...args: unknown[]) => mockFile(...args),
      load: vi.fn(),
      duration: (...args: unknown[]) => mockDur(...args),
      time: (...args: unknown[]) => mockTime(...args),
      isPlaying: (...args: unknown[]) => mockPlaying(...args),
      play: vi.fn(),
      pause: vi.fn(),
      seek: vi.fn(),
      setVolume: vi.fn(),
      setNextProvider: vi.fn(),
      onEnded: vi.fn(),
    },
  }
})

const song = {
  file: 's.mp3',
  artist: 'Artist',
  title: 'Title',
  album: 'Album',
  year: '2020',
  duration: 180,
  coverArt: null,
  dateDownload: null,
  isPodcast: false,
}

function syncWith(over: Partial<PlayerState>) {
  usePlayer.setState({
    currentSong: song as PlayerState['currentSong'],
    isPlaying: true,
    positionSec: 0,
    volume: 0.7,
    shuffle: false,
    repeat: false,
    queue: [],
    source: null,
    isCustomQueue: false,
    firstPlay: false,
    queueTotal: 0,
    ...over,
  })
  usePlayer.getState().syncAudio()
}

beforeEach(() => {
  mockFading.mockReturnValue(false)
})

describe('syncEngine repeat-wrap guard', () => {
  it('does not rewind a finished song when repeat is off', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(180)
    mockTime.mockReturnValue(179.9)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    // Element parked at the edge, server agrees: no drift, no rewind.
    syncWith({ positionSec: 179.9, repeat: false })
    expect(engine.seek).not.toHaveBeenCalled()
  })

  it('rewinds the ended element when repeat is on', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(180)
    mockTime.mockReturnValue(179.9)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    syncWith({ positionSec: 179.9, repeat: true })
    expect(engine.seek).toHaveBeenCalledWith(0)
  })
})

describe('syncEngine drift correction cap', () => {
  it('caps a runaway server estimate short of the duration edge', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(Number.NaN) // metadata pending
    mockTime.mockReturnValue(10)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    syncWith({ positionSec: 5000 })
    // 180 would land exactly on the edge and fire `ended`, advancing away
    // mid-track — stop at the margin so the tail plays out instead.
    expect(engine.seek).toHaveBeenCalledWith(178)
    expect(engine.seek).not.toHaveBeenCalledWith(5000)
  })

  it('passes sane corrections through untouched', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(180)
    mockTime.mockReturnValue(10)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    syncWith({ positionSec: 120 })
    expect(engine.seek).toHaveBeenCalledWith(120)
  })

  it('stops at the margin when the estimate sits on the edge', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(180)
    mockTime.mockReturnValue(10)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    syncWith({ positionSec: 180 })
    expect(engine.seek).toHaveBeenCalledWith(178)
  })

  it('keeps the legacy exact cap for tiny files', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(3)
    mockTime.mockReturnValue(0)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    syncWith({ positionSec: 2.9 })
    expect(engine.seek).toHaveBeenCalledWith(2.9)
  })
})

describe('toggle during a fade', () => {
  const songB = { ...song, file: 'b.mp3', title: 'Next' }

  function pausedB(): PlayerState {
    return {
      currentSong: songB as PlayerState['currentSong'],
      isPlaying: false,
      positionSec: 0,
      volume: 0.7,
      shuffle: false,
      repeat: false,
      queue: [],
      source: null,
      isCustomQueue: false,
      firstPlay: false,
      queueTotal: 0,
    }
  }

  it('pauses instantly even though the element disagrees with the store', async () => {
    // Fade A->B: the store names the incoming track while the element's
    // active slot still holds the outgoing one. Pause must flip instantly
    // instead of waiting out (or losing to) the server round trip.
    mockFading.mockReturnValue(true)
    mockFile.mockReturnValue('a.mp3')
    mockDur.mockReturnValue(200)
    mockTime.mockReturnValue(197)
    mockPlaying.mockReturnValue(true)
    mockToggle.mockResolvedValue(pausedB())
    vi.mocked(engine.pause).mockClear()
    syncWith({ currentSong: songB as PlayerState['currentSong'], isPlaying: true, positionSec: 0 })
    await usePlayer.getState().toggle()
    expect(engine.pause).toHaveBeenCalled()
    expect(usePlayer.getState().isPlaying).toBe(false)
  })

  it('resumes instantly mid-fade', async () => {
    mockFading.mockReturnValue(true)
    mockFile.mockReturnValue('a.mp3')
    mockDur.mockReturnValue(200)
    mockTime.mockReturnValue(197)
    mockPlaying.mockReturnValue(false)
    mockToggle.mockResolvedValue({ ...pausedB(), isPlaying: true })
    vi.mocked(engine.play).mockClear()
    syncWith({ currentSong: songB as PlayerState['currentSong'], isPlaying: false, positionSec: 0 })
    await usePlayer.getState().toggle()
    expect(engine.play).toHaveBeenCalled()
    expect(usePlayer.getState().isPlaying).toBe(true)
  })
})
