import { describe, expect, it, vi } from 'vitest'
import { engine } from './audio/engine'
import { usePlayer } from './store/player'
import type { PlayerState } from './api/client'

const { mockUpdateSettings, mockPlayerState, mockPause, mockSettings, mockSetQueue, mockPlay } = vi.hoisted(() => ({
  mockUpdateSettings: vi.fn(),
  mockPlayerState: vi.fn(),
  mockPause: vi.fn(),
  mockSettings: vi.fn(),
  mockSetQueue: vi.fn(),
  mockPlay: vi.fn(),
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
      setQueue: (...args: unknown[]) => mockSetQueue(...args),
      play: (...args: unknown[]) => mockPlay(...args),
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
    // (setQueue rejects here; playerState is the mocked server truth.)
    mockSetQueue.mockRejectedValueOnce(new Error('no server'))
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

  it('names the clicked song instantly, before the server confirms', async () => {
    // No old-song flash through both round trips: the store flips
    // synchronously (the element already plays it), then the responses
    // confirm. Deferred promises let us observe the in-between state.
    const queued = playerState({ currentSong: song as PlayerState['currentSong'], isPlaying: true })
    const played = playerState({
      currentSong: { ...song, title: 'Server Title' } as PlayerState['currentSong'],
      isPlaying: true,
    })
    let resolveQueue!: (v: unknown) => void
    let resolvePlay!: (v: unknown) => void
    mockSetQueue.mockImplementationOnce(() => new Promise((r) => { resolveQueue = r }))
    mockPlay.mockImplementationOnce(() => new Promise((r) => { resolvePlay = r }))
    usePlayer.setState({ currentSong: episode as PlayerState['currentSong'], isPlaying: true })

    const flight = usePlayer.getState().playSong(song, [song], { type: 'all_songs', id: null })
    // Synchronously after the call (round trips still pending)...
    expect(usePlayer.getState().currentSong?.file).toBe('s.mp3')
    expect(usePlayer.getState().isPlaying).toBe(true)

    resolveQueue(queued)
    await Promise.resolve()
    resolvePlay(played)
    await flight
    expect(usePlayer.getState().currentSong?.title).toBe('Server Title')
  })

  it('never applies the intermediate queue response (still names old track)', async () => {
    // Queue-replace doesn't change the current track server-side, so its
    // response still names the previous song. Applying it flips the UI
    // back mid-flight — the back-and-forth on every song click (next-track
    // clicks have no intermediate response, which is why they never do).
    const oldSong = { ...song, file: 'old.mp3', title: 'Old' }
    const queued = playerState({ currentSong: oldSong as PlayerState['currentSong'], isPlaying: false })
    const played = playerState({ currentSong: song as PlayerState['currentSong'], isPlaying: true })
    let resolveQueue!: (v: unknown) => void
    let resolvePlay!: (v: unknown) => void
    mockSetQueue.mockImplementationOnce(() => new Promise((r) => { resolveQueue = r }))
    mockPlay.mockImplementationOnce(() => new Promise((r) => { resolvePlay = r }))
    usePlayer.setState({ currentSong: oldSong as PlayerState['currentSong'], isPlaying: true })

    const flight = usePlayer.getState().playSong(song, [song], { type: 'all_songs', id: null })
    expect(usePlayer.getState().currentSong?.file).toBe('s.mp3')

    resolveQueue(queued)
    await Promise.resolve()
    await Promise.resolve()
    // Queue resolved mid-flight: UI must still name the clicked song.
    expect(usePlayer.getState().currentSong?.file).toBe('s.mp3')
    expect(usePlayer.getState().isPlaying).toBe(true)

    resolvePlay(played)
    await flight
    expect(usePlayer.getState().currentSong?.file).toBe('s.mp3')
  })

  it('ignores stale responses when clicks overlap (latest wins)', async () => {
    // Rapid clicks interleave round trips (a cover burst can stall fetches
    // for seconds): click A, click B, B resolves first, then A's late
    // responses arrive. They must dissolve — not reload A over B or flip
    // paused back to playing.
    function deferred<T>() {
      let resolve!: (v: T) => void
      const promise = new Promise<T>((r) => {
        resolve = r
      })
      return { promise, resolve }
    }
    const songA = { ...song, file: 'a.mp3', title: 'A' }
    const songB = { ...song, file: 'b.mp3', title: 'B' }
    const dqA = deferred<PlayerState>()
    const dqB = deferred<PlayerState>()
    // A goes stale before ever reaching play: only one play call happens.
    const dpB = deferred<PlayerState>()
    mockSetQueue
      .mockImplementationOnce(() => dqA.promise)
      .mockImplementationOnce(() => dqB.promise)
    mockPlay.mockImplementationOnce(() => dpB.promise)
    usePlayer.setState({ currentSong: null, isPlaying: false })

    const flightA = usePlayer.getState().playSong(songA, [songA], { type: 'all_songs', id: null })
    const flightB = usePlayer.getState().playSong(songB, [songB], { type: 'all_songs', id: null })
    // Newest click already names B optimistically.
    expect(usePlayer.getState().currentSong?.file).toBe('b.mp3')

    // B (newest) resolves fully first...
    dqB.resolve(playerState({ currentSong: songB as PlayerState['currentSong'], isPlaying: true }))
    await Promise.resolve()
    dpB.resolve(playerState({ currentSong: songB as PlayerState['currentSong'], isPlaying: true }))
    await flightB
    expect(usePlayer.getState().currentSong?.file).toBe('b.mp3')

    // ...then A's stale queue response lands late and must change nothing
    // (it returns before even sending play).
    dqA.resolve(playerState({ currentSong: songA as PlayerState['currentSong'], isPlaying: true }))
    await flightA
    expect(mockPlay).toHaveBeenCalledTimes(1)
    expect(usePlayer.getState().currentSong?.file).toBe('b.mp3')
    expect(usePlayer.getState().isPlaying).toBe(true)
    expect(engine.currentFile()).toBe('b.mp3')
  })
})
