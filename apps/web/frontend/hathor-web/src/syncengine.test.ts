import { describe, expect, it, vi } from 'vitest'
import { engine } from './audio/engine'
import { usePlayer } from './store/player'
import type { PlayerState } from './api/client'

const { mockFile, mockDur, mockTime, mockPlaying } = vi.hoisted(() => ({
  mockFile: vi.fn(),
  mockDur: vi.fn(),
  mockTime: vi.fn(),
  mockPlaying: vi.fn(),
}))

vi.mock('./audio/engine', () => ({
  engine: {
    isFading: () => false,
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
}))

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
  it('caps a runaway server estimate at the catalog duration', () => {
    mockFile.mockReturnValue('s.mp3')
    mockDur.mockReturnValue(Number.NaN) // metadata pending
    mockTime.mockReturnValue(10)
    mockPlaying.mockReturnValue(true)
    vi.mocked(engine.seek).mockClear()
    syncWith({ positionSec: 5000 })
    expect(engine.seek).toHaveBeenCalledWith(180)
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
})
