import { fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LyricsSheet from './components/LyricsSheet'
import SyncedLyrics from './components/ui/SyncedLyrics'
import { useActiveChapter } from './components/ui/PodcastTimestamps'
import { usePlayer } from './store/player'
import type { PodcastTimestamp, Song } from './api/client'

const { mockList, mockEngineTime, mockEngineSeek, mockApiSeek, mockSongLyrics, mockSearchLyrics, mockSaveLyrics } = vi.hoisted(() => ({
  mockList: vi.fn(),
  mockEngineTime: vi.fn(),
  mockEngineSeek: vi.fn(),
  mockApiSeek: vi.fn(),
  mockSongLyrics: vi.fn(),
  mockSearchLyrics: vi.fn(),
  mockSaveLyrics: vi.fn(),
  mockLyricsOffset: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      podcastTimestamps: (...args: unknown[]) => mockList(...args),
      seek: (...args: unknown[]) => mockApiSeek(...args),
      songLyrics: (...args: unknown[]) => mockSongLyrics(...args),
      searchLyrics: (...args: unknown[]) => mockSearchLyrics(...args),
      saveLyrics: (...args: unknown[]) => mockSaveLyrics(...args),
      lyricsOffset: async () => ({ offsetMs: 0 }),
    },
  }
})

vi.mock('./audio/engine', () => ({
  engine: {
    time: (...args: unknown[]) => mockEngineTime(...args),
    seek: (...args: unknown[]) => mockEngineSeek(...args),
    // store/player wires these at module scope; no-op here.
    setNextProvider: vi.fn(),
    onEnded: vi.fn(),
    isFading: () => false,
    isPlaying: () => false,
    play: vi.fn(),
    pause: vi.fn(),
  },
}))

const chapters: PodcastTimestamp[] = [
  { id: 1, podcastFile: 'ep.mp3', name: 'Intro', startSecs: 0, endSecs: 60 },
  { id: 2, podcastFile: 'ep.mp3', name: 'Main', startSecs: 60, endSecs: null },
]

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  window.HTMLElement.prototype.scrollTo = vi.fn() as unknown as typeof window.HTMLElement.prototype.scrollTo
  mockList.mockResolvedValue(chapters)
  mockEngineTime.mockReturnValue(0)
  mockApiSeek.mockResolvedValue({})
})

describe('useActiveChapter', () => {
  it('returns the chapter at the engine clock for podcasts', async () => {
    mockEngineTime.mockReturnValue(75)
    const { result } = renderHook(() => useActiveChapter('ep.mp3', true), { wrapper: wrapper() })
    await waitFor(() => {
      expect(result.current?.name).toBe('Main')
    })
    expect(result.current?.startSecs).toBe(60)
  })

  it('returns null for songs even with chapters cached', async () => {
    mockEngineTime.mockReturnValue(75)
    const { result } = renderHook(() => useActiveChapter('s.mp3', false), { wrapper: wrapper() })
    await waitFor(() => {
      expect(mockList).not.toHaveBeenCalled()
    })
    expect(result.current).toBeNull()
  })

  it('returns null when the episode has no chapters', async () => {
    mockList.mockResolvedValue([])
    mockEngineTime.mockReturnValue(75)
    const { result } = renderHook(() => useActiveChapter('ep.mp3', true), { wrapper: wrapper() })
    await waitFor(() => {
      expect(result.current).toBeNull()
    })
  })
})

describe('SyncedLyrics chapter clock', () => {
  const synced = ['[00:05.00] five', '[00:10.00] ten'].join('\n')

  it('rebases highlight to the chapter start', async () => {
    // Engine at 65s into the episode, chapter base 60s → lyric clock 5s.
    mockEngineTime.mockReturnValue(65)
    render(<SyncedLyrics synced={synced} plain={null} isLoading={false} chapterStartSecs={60} />)
    await waitFor(() => {
      expect(screen.getByText('five').className).toContain('text-white')
    })
    expect(screen.getByText('ten').className).not.toContain('text-white')
  })

  it('without a base the same clock highlights the later line', async () => {
    mockEngineTime.mockReturnValue(65)
    render(<SyncedLyrics synced={synced} plain={null} isLoading={false} />)
    await waitFor(() => {
      expect(screen.getByText('ten').className).toContain('text-white')
    })
  })

  it('tap seeks to chapter base plus line time', () => {
    mockEngineTime.mockReturnValue(0)
    render(<SyncedLyrics synced={synced} plain={null} isLoading={false} chapterStartSecs={60} />)
    fireEvent.click(screen.getByText('five'))
    expect(mockEngineSeek).toHaveBeenCalledWith(65)
    expect(mockApiSeek).toHaveBeenCalledWith(65)
  })
})

describe('LyricsSheet chapter save flow', () => {
  const episode: Song = {
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

  function renderSheet() {
    usePlayer.setState({ currentSong: episode })
    return render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <LyricsSheet open onClose={() => {}} />
      </QueryClientProvider>,
    )
  }

  it('saves a picked candidate under the active chapter key', async () => {
    mockSongLyrics.mockResolvedValue({ synced: null, plain: null })
    mockSearchLyrics.mockResolvedValue([
      {
        id: 1, trackName: 'Intro', artistName: '', albumName: null,
        duration: null, syncedLyrics: '[00:01.00] hi', plainLyrics: null,
      },
    ])
    mockSaveLyrics.mockResolvedValue(undefined)
    mockEngineTime.mockReturnValue(10)
    renderSheet()

    // No chapter lyrics yet: search affordance shows; the panel's
    // initial search (chapter title, empty artist) finds the candidate.
    expect(await screen.findByText('No lyrics found.')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Search candidates'))
    expect(mockSearchLyrics).toHaveBeenCalledWith('Intro', '')
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(mockSaveLyrics).toHaveBeenCalledWith('ep.mp3', '[00:01.00] hi', null, 1)
    })
  })
})
