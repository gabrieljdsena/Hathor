import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import PlayerBar from './components/PlayerBar'
import { usePlayer } from './store/player'
import type { Song } from './api/client'

const { mockSeek, getTime, getDuration } = vi.hoisted(() => {
  let fakeTime = 0
  const fakeDuration = 180
  return {
    mockSeek: vi.fn((v: number) => {
      fakeTime = v
    }),
    getTime: () => fakeTime,
    getDuration: () => fakeDuration,
    _setTime: (v: number) => {
      fakeTime = v
    },
  }
})

function setTime(v: number) {
  // Reach into the hoisted closure via a seek (live scrub writes it).
  mockSeek(v)
  mockSeek.mockClear()
}

vi.mock('./audio/engine', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./audio/engine')>()
  return {
    ...mod,
    engine: {
      time: () => getTime(),
      duration: () => getDuration(),
      seek: (v: number) => mockSeek(v),
      setVolume: vi.fn(),
      setCrossfade: vi.fn(),
      setNormalize: vi.fn(),
      ensureContext: vi.fn(),
      currentFile: () => 's.mp3',
      fadeTarget: () => null,
      isFading: () => false,
      isPlaying: () => true,
      play: vi.fn(),
      pause: vi.fn(),
      load: vi.fn(),
      setNextProvider: vi.fn(),
      onEnded: vi.fn(),
      onTailStuck: vi.fn(),
      onReport: undefined,
    },
  }
})

const song: Song = {
  file: 's.mp3',
  artist: 'Artist',
  title: 'Title',
  album: 'Album',
  year: '2020',
  duration: 180,
  coverArt: null,
  dateDownload: null,
}

function renderBar() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <PlayerBar onToggleQueue={() => {}} onToggleLyrics={() => {}} onOpenNowPlaying={() => {}} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
  const slider = document.getElementById('d-progressSlider') as HTMLInputElement
  expect(slider).not.toBeNull()
  return slider
}

beforeEach(() => {
  vi.useFakeTimers()
  setTime(0)
  usePlayer.setState({ currentSong: song, isPlaying: true })
})

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe('ProgressSlider ticking after a seek', () => {
  it('keeps moving the thumb after a pointer scrub even while focused', async () => {
    const slider = renderBar()
    // Pre-scrub tick writes the position.
    setTime(10)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('10')

    // Drag to 60 and release. The input keeps focus after release (as in a
    // real browser) — the thumb must still track playback afterwards.
    fireEvent.pointerDown(slider)
    fireEvent.change(slider, { target: { value: '60' } })
    expect(mockSeek).toHaveBeenCalledWith(60)
    slider.focus()
    fireEvent.pointerUp(slider)

    // Playback continues; the ticker must resume writing the thumb without
    // waiting for a blur click elsewhere.
    setTime(61)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('61')
    setTime(62)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('62')
  })

  it('holds the thumb only mid-drag, then resumes on release', async () => {
    const slider = renderBar()
    setTime(10)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('10')

    // Mid-drag the ticker must not fight the user's thumb.
    fireEvent.pointerDown(slider)
    fireEvent.change(slider, { target: { value: '60' } })
    setTime(60)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('60')

    fireEvent.pointerUp(slider)
    setTime(61)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('61')
  })

  it('resumes after keyboard arrow seeks', async () => {
    const slider = renderBar()
    slider.focus()
    setTime(10)
    await vi.advanceTimersByTimeAsync(600)

    fireEvent.keyDown(slider, { key: 'ArrowRight' })
    fireEvent.change(slider, { target: { value: '20' } })
    fireEvent.keyUp(slider, { key: 'ArrowRight' })

    setTime(21)
    await vi.advanceTimersByTimeAsync(600)
    expect(slider.value).toBe('21')
  })
})
