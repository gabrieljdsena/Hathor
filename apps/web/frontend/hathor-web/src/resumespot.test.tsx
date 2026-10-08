import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ResumeSpot from './components/ui/ResumeSpot'
import { usePlayer } from './store/player'
import type { PlaybackSpot, Song } from './api/client'

const { mockLatest, mockSong, mockSeek, mockEngineSeek, mockPlaySong } = vi.hoisted(() => ({
  mockLatest: vi.fn(),
  mockSong: vi.fn(),
  mockSeek: vi.fn(),
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
      seek: (...args: unknown[]) => mockSeek(...args),
    },
  }
})

vi.mock('./audio/engine', () => ({
  engine: {
    time: () => 0,
    seek: (...args: unknown[]) => mockEngineSeek(...args),
    // store/player wires these at module scope; no-op here.
    setNextProvider: vi.fn(),
    onEnded: vi.fn(),
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

const spot: PlaybackSpot = {
  userKey: 'desktop',
  file: 's.mp3',
  positionSec: 95,
  isPodcast: false,
  device: 'Desktop',
  updatedUtc: '2026-10-08T00:00:00Z',
}

function renderSpot(s: PlaybackSpot) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ResumeSpot spot={s} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  usePlayer.setState({ currentSong: null, isPlaying: false })
  usePlayer.setState({ playSong: mockPlaySong } as Partial<ReturnType<typeof usePlayer.getState>>)
  mockPlaySong.mockResolvedValue(undefined)
  mockSong.mockResolvedValue(song)
  mockSeek.mockResolvedValue({})
})

describe('ResumeSpot', () => {
  it('tapping plays the file at the foreign position, then dismisses', async () => {
    renderSpot(spot)
    const label = 'Resume s.mp3 at 1:35 from Desktop'
    expect(await screen.findByText(label)).toBeInTheDocument()

    fireEvent.click(screen.getByText(label))
    await waitFor(() => expect(mockPlaySong).toHaveBeenCalledWith(
      song, [song], { type: 'all_songs', id: null },
    ))
    expect(mockEngineSeek).toHaveBeenCalledWith(95)
    expect(mockSeek).toHaveBeenCalledWith(95)
    await waitFor(() => expect(screen.queryByText(label)).toBeNull())
  })

  it('stays hidden when the spot names what is already playing', () => {
    usePlayer.setState({ currentSong: song })
    const { container } = renderSpot(spot)
    expect(container.firstChild).toBeNull()
  })
})
