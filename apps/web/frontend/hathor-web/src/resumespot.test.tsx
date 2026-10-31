import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ResumeBento, { resumeFromSpot } from './components/ui/ResumeBento'
import { useResumeSpot } from './components/ui/ResumeSpot'
import { pushResumeSpot, usePlayer } from './store/player'
import type { PlaybackSpot, Song } from './api/client'

const {
  mockLatest,
  mockSong,
  mockPodcastDetails,
  mockSeek,
  mockPushPlayback,
  mockEngineSeek,
  mockPlaySong,
} = vi.hoisted(() => ({
  mockLatest: vi.fn(),
  mockSong: vi.fn(),
  mockPodcastDetails: vi.fn(),
  mockSeek: vi.fn(),
  mockPushPlayback: vi.fn(),
  mockEngineSeek: vi.fn(),
  mockPlaySong: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      latestPlayback: (...args: unknown[]) => mockLatest(...args),
      song: (...args: unknown[]) => mockSong(...args),
      podcastDetails: (...args: unknown[]) => mockPodcastDetails(...args),
      seek: (...args: unknown[]) => mockSeek(...args),
      pushPlayback: (...args: unknown[]) => mockPushPlayback(...args),
    },
  }
})

vi.mock('./audio/engine', () => ({
  engine: {
    time: () => 0,
    seek: (...args: unknown[]) => mockEngineSeek(...args),
    // store/player wires these at module scope; no-op here.
      setNextProvider: vi.fn(),
      setStreamUrlProvider: vi.fn(),
      onEnded: vi.fn(),
      onTailStuck: vi.fn(),
      onBuffering: vi.fn(),
      onFatal: vi.fn(),
    onReport: undefined,
    currentFile: () => null,
    isFading: () => false,
    isPlaying: () => false,
    play: vi.fn(),
    pause: vi.fn(),
  },
}))

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

const nextSong: Song = {
  file: 'n.mp3',
  artist: 'Other',
  title: 'Next',
  album: 'Album',
  year: '2021',
  duration: 200,
  coverArt: null,
  dateDownload: null,
}

const spot: PlaybackSpot = {
  userKey: 'phone-9',
  file: 's.mp3',
  positionSec: 95,
  isPodcast: false,
  device: 'Android',
  updatedUtc: '2026-10-08T00:00:00Z',
  queueFiles: ['s.mp3', 'n.mp3'],
  queueSource: { type: 'all_songs', id: null },
}

function renderBento(s: PlaybackSpot | null, variant?: 'bento' | 'banner') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ResumeBento spot={s} variant={variant} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  localStorage.setItem('hathor:device-id', 'browser-1')
  usePlayer.setState({ currentSong: null, isPlaying: false, queue: [] })
  usePlayer.setState({ playSong: mockPlaySong } as Partial<ReturnType<typeof usePlayer.getState>>)
  mockPlaySong.mockResolvedValue(undefined)
  mockPushPlayback.mockResolvedValue(true)
  mockSong.mockImplementation((file: unknown) =>
    Promise.resolve(file === 'n.mp3' ? nextSong : song),
  )
  mockSeek.mockResolvedValue({})
})

describe('ResumeBento', () => {
  it('opens a dark-orange menu listing the device first, then the song', async () => {
    const { container } = renderBento(spot)
    const button = await screen.findByRole('button', { name: 'Resume from another device' })
    // Orange bento trigger, never blue.
    expect(button.className).toMatch(/orange/)
    expect(button.className).not.toMatch(/sky/)
    expect(container.innerHTML).not.toMatch(/sky-/)

    fireEvent.click(button)
    const menu = await screen.findByRole('menu', { name: 'Resume from another device' })
    expect(menu.className).toMatch(/bg-zinc-900/)
    expect(menu.className).toMatch(/orange/)
    expect(menu.innerHTML).not.toMatch(/sky-/)
    // Device header precedes the song title in the menu.
    const devicePos = menu.innerHTML.indexOf('From Android')
    const songPos = menu.innerHTML.indexOf('Title')
    expect(devicePos).toBeGreaterThanOrEqual(0)
    expect(songPos).toBeGreaterThan(devicePos)
    expect(await screen.findByText('From Android')).toBeInTheDocument()
    expect(await screen.findByText('Title — Artist')).toBeInTheDocument()
  })

  it('banner shows the device, track and saved time without the dots trigger', async () => {
    renderBento(spot, 'banner')
    expect(await screen.findByText('Paused on Android')).toBeInTheDocument()
    expect(await screen.findByText('Title — Artist · 1:35')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Resume from another device' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Resume at 1:35' }))

    await waitFor(() =>
      expect(mockPlaySong).toHaveBeenCalledWith(
        song,
        [song, nextSong],
        { type: 'all_songs', id: null },
      ),
    )
    expect(mockEngineSeek).toHaveBeenCalledWith(95)
    expect(mockSeek).toHaveBeenCalledWith(95)
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Resume at 1:35' })).toBeNull(),
    )
  })

  it('tapping resume restores the full foreign queue at the saved position', async () => {
    renderBento(spot)
    fireEvent.click(await screen.findByRole('button', { name: 'Resume from another device' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Resume at 1:35' }))

    await waitFor(() =>
      expect(mockPlaySong).toHaveBeenCalledWith(
        song,
        [song, nextSong],
        { type: 'all_songs', id: null },
      ),
    )
    expect(mockEngineSeek).toHaveBeenCalledWith(95)
    expect(mockSeek).toHaveBeenCalledWith(95)
    // Self-dismisses after resuming.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Resume from another device' })).toBeNull(),
    )
  })

  it('falls back to the raw file key when the library lookup fails', async () => {
    mockSong.mockRejectedValue(new Error('gone'))
    renderBento(spot)
    fireEvent.click(await screen.findByRole('button', { name: 'Resume from another device' }))
    expect(await screen.findByText('s.mp3')).toBeInTheDocument()
  })

  it('stays hidden when the spot names what is already playing', () => {
    usePlayer.setState({ currentSong: song })
    const { container } = renderBento(spot)
    expect(container.firstChild).toBeNull()
    expect(mockSong).not.toHaveBeenCalled()
  })

  it('resumeFromSpot falls back to single-song for legacy spots without a queue', async () => {
    const legacy = { ...spot, queueFiles: null, queueSource: null }
    await resumeFromSpot(legacy, mockPlaySong)
    expect(mockPlaySong).toHaveBeenCalledWith(song, [song], { type: 'all_songs', id: null })
    expect(mockSeek).toHaveBeenCalledWith(95)
  })

  it('polls with its own device id excluded', async () => {
    mockLatest.mockResolvedValue(null)
    function Probe() {
      const found = useResumeSpot()
      return <div>{found ? found.file : 'none'}</div>
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={client}>
        <Probe />
      </QueryClientProvider>,
    )
    await waitFor(() => expect(mockLatest).toHaveBeenCalledWith('browser-1'))
    expect(await screen.findByText('none')).toBeInTheDocument()
  })
})

describe('pushResumeSpot', () => {
  it('snapshots the queue (current first) plus source when pausing', () => {
    usePlayer.setState({
      currentSong: song,
      isPlaying: false,
      queue: [nextSong],
      source: { type: 'playlist', id: '7' },
    })
    pushResumeSpot()
    expect(mockPushPlayback).toHaveBeenCalledWith(
      'browser-1',
      expect.objectContaining({
        file: 's.mp3',
        queueFiles: ['s.mp3', 'n.mp3'],
        queueSource: { type: 'playlist', id: '7' },
      }),
    )
  })

  it('pushes nothing while playing', () => {
    usePlayer.setState({ currentSong: song, isPlaying: true, queue: [nextSong] })
    pushResumeSpot()
    expect(mockPushPlayback).not.toHaveBeenCalled()
  })
})
