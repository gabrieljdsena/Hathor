import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { engine } from './audio/engine'
import {
  cancelPlaybackIntent,
  hasPendingPlaybackIntent,
  resetAutoAdvanceStreak,
  usePlayer,
} from './store/player'
import type { PlayerState, Song } from './api/client'

const {
  mockPause,
  mockResume,
  mockPlayerState,
  mockPushPlayback,
  mockSetQueue,
  mockPlay,
  mockNext,
  engineHandlers,
} = vi.hoisted(() => ({
  mockPause: vi.fn(),
  mockResume: vi.fn(),
  mockPlayerState: vi.fn(),
  mockPushPlayback: vi.fn(),
  mockSetQueue: vi.fn(),
  mockPlay: vi.fn(),
  mockNext: vi.fn(),
  engineHandlers: {
    buffering: undefined as ((buffering: boolean) => void) | undefined,
    fatal: undefined as ((file: string, reason: string) => void) | undefined,
  },
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      pause: (...args: unknown[]) => mockPause(...args),
      resume: (...args: unknown[]) => mockResume(...args),
      playerState: (...args: unknown[]) => mockPlayerState(...args),
      pushPlayback: (...args: unknown[]) => mockPushPlayback(...args),
      setQueue: (...args: unknown[]) => mockSetQueue(...args),
      play: (...args: unknown[]) => mockPlay(...args),
      next: (...args: unknown[]) => mockNext(...args),
    },
  }
})

vi.mock('./audio/engine', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./audio/engine')>()
  return {
    ...mod,
    engine: {
      ensureContext: vi.fn(),
      time: vi.fn(() => 42),
      duration: vi.fn(() => 180),
      seek: vi.fn(),
      load: vi.fn(),
      play: vi.fn(),
      pause: vi.fn(),
      setVolume: vi.fn(),
      setNormalize: vi.fn(),
      setCrossfade: vi.fn(),
      currentFile: vi.fn(() => 's.mp3'),
      fadeTarget: vi.fn(() => null),
      isFading: vi.fn(() => false),
      isPlaying: vi.fn(() => false),
      driftTarget: vi.fn(() => null),
      lastGoodPosition: vi.fn(() => 0),
      setNextProvider: vi.fn(),
      setStreamUrlProvider: vi.fn(),
      retryNow: vi.fn(),
      onEnded: vi.fn(),
      onTailStuck: vi.fn(),
      onBuffering: vi.fn((cb: (buffering: boolean) => void) => {
        engineHandlers.buffering = cb
      }),
      onFatal: vi.fn((cb: (file: string, reason: string) => void) => {
        engineHandlers.fatal = cb
      }),
      onReport: undefined,
    },
  }
})

const songA: Song = {
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

const songB: Song = { ...songA, file: 'b.mp3', title: 'Next' }

function playerState(over: Partial<PlayerState> = {}): PlayerState {
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
    queueVersion: 0,
    ...over,
  }
}

async function flush() {
  await Promise.resolve()
  await Promise.resolve()
}

beforeEach(() => {
  cancelPlaybackIntent()
  vi.useFakeTimers()
  vi.clearAllMocks()
  resetAutoAdvanceStreak()
  mockPushPlayback.mockResolvedValue(undefined)
  vi.mocked(engine.time).mockReturnValue(42)
  vi.mocked(engine.isFading).mockReturnValue(false)
  vi.mocked(engine.currentFile).mockReturnValue('s.mp3')
  vi.mocked(engine.isPlaying).mockReturnValue(false)
  vi.mocked(engine.driftTarget).mockReturnValue(null)
  usePlayer.setState(
    playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: true }),
  )
})

afterEach(() => {
  cancelPlaybackIntent()
  vi.useRealTimers()
})

describe('reliable playback intents', () => {
  it('sends the frozen position and file, then retries without flipping back', async () => {
    mockPause
      .mockRejectedValueOnce(new Error('Network error: fetch failed'))
      .mockResolvedValueOnce(
        playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: false }),
      )

    await usePlayer.getState().toggle()

    expect(mockPause).toHaveBeenNthCalledWith(1, 42, 's.mp3')
    expect(usePlayer.getState().isPlaying).toBe(false)
    expect(usePlayer.getState().syncing).toBe(true)
    expect(hasPendingPlaybackIntent()).toBe(true)

    await vi.advanceTimersByTimeAsync(500)
    await flush()

    expect(mockPause).toHaveBeenNthCalledWith(2, 42, 's.mp3')
    expect(usePlayer.getState().isPlaying).toBe(false)
    expect(usePlayer.getState().syncing).toBe(false)
    expect(hasPendingPlaybackIntent()).toBe(false)
  })

  it('resumes with the idempotent endpoint instead of a blind toggle', async () => {
    usePlayer.setState(playerState({ currentSong: songA as PlayerState['currentSong'] }))
    mockResume.mockResolvedValue(
      playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: true }),
    )

    await usePlayer.getState().toggle()

    expect(mockResume).toHaveBeenCalledOnce()
    expect(mockPause).not.toHaveBeenCalled()
    expect(usePlayer.getState().isPlaying).toBe(true)
    expect(hasPendingPlaybackIntent()).toBe(false)
  })

  it('cancels a pending pause retry when the user resumes', async () => {
    mockPause.mockRejectedValue(new Error('offline'))
    mockResume.mockResolvedValue(
      playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: true }),
    )

    await usePlayer.getState().toggle()
    expect(hasPendingPlaybackIntent()).toBe(true)

    await usePlayer.getState().toggle()
    expect(hasPendingPlaybackIntent()).toBe(false)
    expect(mockResume).toHaveBeenCalledOnce()

    await vi.advanceTimersByTimeAsync(5000)
    await flush()
    expect(mockPause).toHaveBeenCalledTimes(1)
    expect(usePlayer.getState().isPlaying).toBe(true)
  })

  it('never lets a refresh resurrect audio against a pending pause', async () => {
    mockPause.mockRejectedValue(new Error('offline'))
    mockPlayerState.mockResolvedValue(
      playerState({
        currentSong: songA as PlayerState['currentSong'],
        isPlaying: true,
        positionSec: 80,
      }),
    )

    await usePlayer.getState().toggle()
    expect(hasPendingPlaybackIntent()).toBe(true)

    await usePlayer.getState().refresh()

    expect(mockPlayerState).toHaveBeenCalledOnce()
    expect(usePlayer.getState().isPlaying).toBe(false)
    expect(usePlayer.getState().positionSec).toBe(80)
  })

  it('cancels a pending pause retry when a new song starts', async () => {
    mockPause.mockRejectedValue(new Error('offline'))
    mockSetQueue.mockResolvedValue(undefined)
    mockPlay.mockResolvedValue(
      playerState({ currentSong: songB as PlayerState['currentSong'], isPlaying: true }),
    )

    await usePlayer.getState().toggle()
    expect(hasPendingPlaybackIntent()).toBe(true)

    await usePlayer.getState().playSong(songB, [songB], { type: 'all_songs', id: null })
    expect(hasPendingPlaybackIntent()).toBe(false)
    expect(usePlayer.getState().currentSong?.file).toBe('b.mp3')

    await vi.advanceTimersByTimeAsync(5000)
    await flush()
    expect(mockPause).toHaveBeenCalledTimes(1)
  })

  it('reconciles on a permanent failure instead of retrying forever', async () => {
    mockPause.mockRejectedValue(new Error('Forbidden'))
    mockPlayerState.mockResolvedValue(
      playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: true }),
    )

    await usePlayer.getState().toggle()
    await flush()

    expect(mockPause).toHaveBeenCalledOnce()
    expect(mockPlayerState).toHaveBeenCalledOnce()
    expect(hasPendingPlaybackIntent()).toBe(false)
    expect(usePlayer.getState().isPlaying).toBe(true)

    await vi.advanceTimersByTimeAsync(5000)
    expect(mockPause).toHaveBeenCalledOnce()
  })

  it('does not seek to the outgoing position while pausing mid-fade', async () => {
    vi.mocked(engine.isFading).mockReturnValue(true)
    usePlayer.setState(playerState({ currentSong: songB as PlayerState['currentSong'], isPlaying: true }))
    mockPause.mockResolvedValue(
      playerState({ currentSong: songB as PlayerState['currentSong'], isPlaying: false }),
    )

    await usePlayer.getState().toggle()

    expect(mockPause).toHaveBeenCalledWith(null, 'b.mp3')
  })

  it('retries immediately when the browser comes back online', async () => {
    mockPause
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(
        playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: false }),
      )

    await usePlayer.getState().toggle()
    expect(hasPendingPlaybackIntent()).toBe(true)

    window.dispatchEvent(new Event('online'))
    await flush()

    expect(mockPause).toHaveBeenCalledTimes(2)
    expect(hasPendingPlaybackIntent()).toBe(false)
  })

  it('drives the buffering indicator from the engine', () => {
    expect(engineHandlers.buffering).toBeDefined()
    engineHandlers.buffering!(true)
    expect(usePlayer.getState().buffering).toBe(true)
    engineHandlers.buffering!(false)
    expect(usePlayer.getState().buffering).toBe(false)
  })
})

describe('fatal media errors', () => {
  it('advances past a dead file', async () => {
    mockNext.mockResolvedValue(
      playerState({ currentSong: songB as PlayerState['currentSong'], isPlaying: true }),
    )

    engineHandlers.fatal?.('s.mp3', 'decode')

    await flush()
    expect(mockNext).toHaveBeenCalledOnce()
    expect(usePlayer.getState().currentSong?.file).toBe('b.mp3')
  })

  it('does not fight a pending transport intent', async () => {
    usePlayer.setState(playerState({ currentSong: songA as PlayerState['currentSong'] }))
    mockResume.mockRejectedValue(new Error('offline'))

    await usePlayer.getState().toggle()
    expect(hasPendingPlaybackIntent()).toBe(true)
    expect(usePlayer.getState().isPlaying).toBe(true)

    engineHandlers.fatal?.('s.mp3', 'decode')

    await flush()
    expect(mockNext).not.toHaveBeenCalled()
  })
})
