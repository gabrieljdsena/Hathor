import { describe, expect, it, vi } from 'vitest'
import { engine } from './audio/engine'
import { usePlayer } from './store/player'
import type { PlayerState } from './api/client'

const { mockUpdateSettings, mockPlayerState, mockPause, mockSettings } = vi.hoisted(() => ({
  mockUpdateSettings: vi.fn(),
  mockPlayerState: vi.fn(),
  mockPause: vi.fn(),
  mockSettings: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      updateSettings: (...args: unknown[]) => mockUpdateSettings(...args),
      playerState: (...args: unknown[]) => mockPlayerState(...args),
      pause: (...args: unknown[]) => mockPause(...args),
      settings: (...args: unknown[]) => mockSettings(...args),
    },
  }
})

describe('normalize preference (browser-local)', () => {
  it('persists to localStorage and drives the engine', () => {
    usePlayer.getState().setNormalize(true)
    expect(localStorage.getItem('hathor:normalize')).toBe('1')
    expect(engine.isNormalize()).toBe(true)

    usePlayer.getState().setNormalize(false)
    expect(localStorage.getItem('hathor:normalize')).toBe('0')
    expect(engine.isNormalize()).toBe(false)
  })
})

describe('boot (page refresh)', () => {
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

  it('pauses server playback left running instead of resuming it', async () => {
    mockPlayerState.mockResolvedValue(
      playerState({ isPlaying: true, firstPlay: false, currentSong: song as PlayerState['currentSong'] }),
    )
    mockPause.mockImplementation(async () =>
      playerState({ isPlaying: false, firstPlay: false, currentSong: song as PlayerState['currentSong'] }),
    )
    mockSettings.mockResolvedValue({ crossfadeEnabled: false, crossfadeSeconds: 5, chapterSkip: false })
    await usePlayer.getState().boot()
    expect(mockPause).toHaveBeenCalledOnce()
    expect(usePlayer.getState().isPlaying).toBe(false)
  })

  it('leaves an already-paused server alone', async () => {
    mockPlayerState.mockResolvedValue(playerState({ isPlaying: false }))
    mockPause.mockClear()
    mockSettings.mockResolvedValue({ crossfadeEnabled: false, crossfadeSeconds: 5, chapterSkip: false })
    await usePlayer.getState().boot()
    expect(mockPause).not.toHaveBeenCalled()
    expect(usePlayer.getState().isPlaying).toBe(false)
  })
})

describe('chapter skip preference (local + account-wide)', () => {
  it('persists to localStorage and to server settings', () => {
    mockUpdateSettings.mockResolvedValue({})
    usePlayer.getState().setChapterSkip(true)
    expect(usePlayer.getState().chapterSkip).toBe(true)
    expect(localStorage.getItem('hathor:chapterskip')).toBe('1')
    expect(mockUpdateSettings).toHaveBeenCalledWith({ chapterSkip: true })

    usePlayer.getState().setChapterSkip(false)
    expect(usePlayer.getState().chapterSkip).toBe(false)
    expect(mockUpdateSettings).toHaveBeenCalledWith({ chapterSkip: false })
  })
})

describe('playSong failure', () => {
  const episode = {
    file: 'ep.mp3',
    artist: 'Host',
    title: 'Episode',
    album: '',
    year: '',
    duration: 900,
    coverArt: null,
    dateDownload: null,
    isPodcast: true,
  }
  const song = { ...episode, file: 's.mp3', title: 'Title', isPodcast: false }

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

  it('reconverges on the server instead of leaving store and element diverged', async () => {
    // Store names the old podcast while the element already plays the new
    // song (optimistic load runs before the server confirms): a failed
    // play call must pull server truth back instead of stranding the pair
    // diverged — divergence is what armed chapter auto-skip on songs.
    // (setQueue/play are the real client here and reject on the relative
    // URL with no server; playerState is the mocked server truth.)
    usePlayer.setState({ currentSong: episode as PlayerState['currentSong'] })
    mockPlayerState.mockResolvedValue(
      playerState({ currentSong: episode as PlayerState['currentSong'], isPlaying: false }),
    )
    await expect(
      usePlayer.getState().playSong(song, [song], { type: 'all_songs', id: null }),
    ).rejects.toThrow()
    expect(mockPlayerState).toHaveBeenCalled()
    expect(usePlayer.getState().currentSong?.file).toBe('ep.mp3')
  })
})
