import { renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { usePlayerHub } from './playerHub'
import { usePlayer } from './store/player'
import type { PlayerState, Song } from './api/client'

const { mockBuild, mockStart, mockStop, handlers, mockLoad, mockPause, mockSetQueue } = vi.hoisted(() => ({
  mockBuild: vi.fn(),
  mockStart: vi.fn(),
  mockStop: vi.fn(),
  handlers: new Map<string, (...args: never[]) => void>(),
  mockLoad: vi.fn(),
  mockPause: vi.fn(),
  mockSetQueue: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      setQueue: (...args: unknown[]) => mockSetQueue(...args),
    },
  }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn(function (this: unknown) {
    mockBuild()
    return {
      withUrl: vi.fn().mockReturnThis(),
      withAutomaticReconnect: vi.fn().mockReturnThis(),
      configureLogging: vi.fn().mockReturnThis(),
      build: () => ({
        on: (name: string, cb: (...args: never[]) => void) => {
          handlers.set(name, cb)
        },
        onreconnected: vi.fn(),
        start: (...args: unknown[]) => mockStart(...args),
        stop: (...args: unknown[]) => mockStop(...args),
      }),
    }
  }),
  LogLevel: { Warning: 3 },
}))

vi.mock('./audio/engine', () => ({
  engine: {
    ensureContext: vi.fn(),
    time: () => 0,
    duration: () => 0,
    seek: vi.fn(),
    load: (...args: unknown[]) => mockLoad(...args),
    play: vi.fn(),
    pause: (...args: unknown[]) => mockPause(...args),
    setVolume: vi.fn(),
      setNextProvider: vi.fn(),
      onEnded: vi.fn(),
      onTailStuck: vi.fn(),
    onReport: undefined,
    currentFile: () => null,
    fadeTarget: () => null,
    isFading: () => false,
    isPlaying: () => false,
  },
}))

function playerState(over: Partial<PlayerState>): PlayerState {
  return {
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
    ...over,
  }
}

const song: Song = {
  file: 'new.mp3',
  artist: 'Artist',
  title: 'Title',
  album: 'Album',
  year: '2020',
  duration: 180,
  coverArt: null,
  dateDownload: null,
  isPodcast: false,
}

beforeEach(() => {
  vi.clearAllMocks()
  handlers.clear()
  // Real HubConnection.start()/stop() return promises; the hook chains
  // .catch() on them, so the fakes must too.
  mockStart.mockResolvedValue(undefined)
  mockStop.mockResolvedValue(undefined)
  localStorage.clear()
  localStorage.setItem('hathor:token', 'tok123')
  usePlayer.setState(playerState({}))
})

describe('usePlayerHub', () => {
  it('connects when logged in and applies broadcast state', () => {
    const { unmount } = renderHook(() => usePlayerHub('user'))
    expect(mockBuild).toHaveBeenCalledTimes(1)
    expect(mockStart).toHaveBeenCalledTimes(1)

    const onState = handlers.get('PlaybackStateChanged')
    expect(onState).toBeDefined()
    onState!(playerState({ currentSong: song as PlayerState['currentSong'], isPlaying: true }) as never)
    // Store converges and the engine renders it (load with autoplay).
    expect(usePlayer.getState().currentSong?.file).toBe('new.mp3')
    expect(mockLoad).toHaveBeenCalled()
    unmount()
    expect(mockStop).toHaveBeenCalledTimes(1)
  })

  it('pauses local audio on a foreign pause broadcast', () => {
    usePlayer.setState(playerState({ currentSong: song as PlayerState['currentSong'], isPlaying: true }))
    renderHook(() => usePlayerHub('user'))
    const onState = handlers.get('PlaybackStateChanged')
    onState!(playerState({ currentSong: song as PlayerState['currentSong'], isPlaying: false }) as never)
    expect(usePlayer.getState().isPlaying).toBe(false)
  })

  it('applies queue updates without touching the current song', () => {
    usePlayer.setState(playerState({ currentSong: song as PlayerState['currentSong'] }))
    renderHook(() => usePlayerHub('user'))
    const onQueue = handlers.get('QueueUpdated')
    expect(onQueue).toBeDefined()
    onQueue!([{ ...song, file: 'q.mp3' }] as never)
    expect(usePlayer.getState().queue).toHaveLength(1)
    expect(usePlayer.getState().currentSong?.file).toBe('new.mp3')
  })

  it('stays disconnected when logged out', () => {
    renderHook(() => usePlayerHub(null))
    expect(mockBuild).not.toHaveBeenCalled()
  })

  it('ignores broadcasts while our own transport is in flight', async () => {
    // Our playSong flight is pending (setQueue deferred): a broadcast
    // naming the old paused track must not revert the optimistic state —
    // that echo is the back-and-forth on every song click.
    const oldSong = { ...song, file: 'old.mp3', title: 'Old' }
    let resolveQueue!: (v: unknown) => void
    mockSetQueue.mockImplementationOnce(() => new Promise((r) => { resolveQueue = r }))
    renderHook(() => usePlayerHub('user'))
    usePlayer.setState(playerState({ currentSong: oldSong as PlayerState['currentSong'], isPlaying: true }))

    const flight = usePlayer.getState().playSong(song, [song], { type: 'all_songs', id: null })
    expect(usePlayer.getState().currentSong?.file).toBe('new.mp3')

    const onState = handlers.get('PlaybackStateChanged')
    onState!(playerState({ currentSong: oldSong as PlayerState['currentSong'], isPlaying: false }) as never)
    expect(usePlayer.getState().currentSong?.file).toBe('new.mp3')
    expect(usePlayer.getState().isPlaying).toBe(true)

    // Settle the flight so module counters reset (play rejects with no
    // server here; the catch reconverges and the error is swallowed).
    resolveQueue(playerState({}))
    await flight.catch(() => {})
  })
})
