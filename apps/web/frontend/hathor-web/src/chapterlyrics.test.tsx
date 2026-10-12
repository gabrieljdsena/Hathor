import { fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LyricsSheet from './components/LyricsSheet'
import SyncedLyrics from './components/ui/SyncedLyrics'
import { useActiveChapter } from './components/ui/PodcastTimestamps'
import { usePlayer } from './store/player'
import type { PodcastTimestamp, Song } from './api/client'

const { mockList, mockEngineTime, mockEngineSeek, mockApiSeek, mockSongLyrics, mockSearchLyrics, mockSaveLyrics, mockSetOffset } = vi.hoisted(() => ({
  mockList: vi.fn(),
  mockEngineTime: vi.fn(),
  mockEngineSeek: vi.fn(),
  mockApiSeek: vi.fn(),
  mockSongLyrics: vi.fn(),
  mockSearchLyrics: vi.fn(),
  mockSaveLyrics: vi.fn(),
  mockLyricsOffset: vi.fn(),
  mockSetOffset: vi.fn(async (_f: string, ms: number, _c?: number | null) => ({ offsetMs: ms })),
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
      setLyricsOffset: (f: string, ms: number, c?: number | null) => mockSetOffset(f, ms, c),
    },
  }
})

vi.mock('./audio/engine', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./audio/engine')>()
  return {
    // Real pure helpers (clampSeekTarget): only the engine singleton is fake.
    ...mod,
    engine: {
      time: (...args: unknown[]) => mockEngineTime(...args),
      seek: (...args: unknown[]) => mockEngineSeek(...args),
      duration: () => 0,
      // store/player wires these at module scope; no-op here.
      setNextProvider: vi.fn(),
      onEnded: vi.fn(),
      onTailStuck: vi.fn(),
      isFading: () => false,
      isPlaying: () => false,
      play: vi.fn(),
      pause: vi.fn(),
    },
  }
})

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
    expect(await screen.findByText('No lyrics saved for “Intro” yet.')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Search candidates'))
    expect(mockSearchLyrics).toHaveBeenCalledWith('Intro', '')
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(mockSaveLyrics).toHaveBeenCalledWith('ep.mp3', '[00:01.00] hi', null, 1)
    })
  })

  it('prefills manual search with the split chapter title and artist', async () => {    // Dashed chapters must not go out as track="Beggin - Frankie Valli":
    // the backend would read the halves backwards.
    mockList.mockResolvedValue([
      { id: 3, podcastFile: 'ep.mp3', name: 'Beggin - Frankie Valli', startSecs: 0, endSecs: null },
    ])
    mockSongLyrics.mockResolvedValue({ synced: null, plain: null })
    mockSearchLyrics.mockResolvedValue([])
    mockEngineTime.mockReturnValue(10)
    renderSheet()

    expect(await screen.findByText('No lyrics saved for “Beggin - Frankie Valli” yet.')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Search candidates'))
    expect(mockSearchLyrics).toHaveBeenCalledWith('Beggin', 'Frankie Valli')
  })

  it('saves under the picked chapter instead of the active one', async () => {
    mockSongLyrics.mockResolvedValue({ synced: null, plain: null })
    mockSearchLyrics.mockResolvedValue([
      {
        id: 1, trackName: 'Intro', artistName: '', albumName: null,
        duration: null, syncedLyrics: '[00:01.00] hi', plainLyrics: null,
      },
    ])
    mockSaveLyrics.mockResolvedValue(undefined)
    mockEngineTime.mockReturnValue(10) // Intro (id 1) is active
    renderSheet()

    fireEvent.click(await screen.findByText('Search candidates'))
    // Picker defaults to the active chapter…
    const select = screen.getByLabelText('Save lyrics to chapter') as HTMLSelectElement
    expect(select.value).toBe('1')
    // …but the lyrics are for Main: pick it (prefill follows the pick).
    fireEvent.change(select, { target: { value: '2' } })
    expect(screen.getByPlaceholderText('Track name')).toHaveValue('Main')
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(mockSaveLyrics).toHaveBeenCalledWith('ep.mp3', '[00:01.00] hi', null, 2)
    })
    // The banner offers the chapter; jumping seeks to its start and closes.
    fireEvent.click(await screen.findByText('Jump to chapter'))
    expect(mockApiSeek).toHaveBeenCalledWith(60)
    await waitFor(() => {
      expect(screen.queryByLabelText('Save lyrics to chapter')).toBeNull()
    })
  })

    it('omits the picker for songs without chapters', async () => {
    mockSongLyrics.mockResolvedValue({ synced: null, plain: null })
    mockSearchLyrics.mockResolvedValue([])
    usePlayer.setState({
      currentSong: {
        file: 's.mp3', artist: 'Artist', title: 'Title', album: 'Album',
        year: '2020', duration: 180, coverArt: null, dateDownload: null,
      },
    })
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <LyricsSheet open onClose={() => {}} />
      </QueryClientProvider>,
    )
    fireEvent.click(await screen.findByText('Search candidates'))
    expect(await screen.findByPlaceholderText('Track name')).toBeInTheDocument()
    expect(screen.queryByLabelText('Save lyrics to chapter')).toBeNull()
  })

  it('closes the panel when saving for the active chapter', async () => {    mockSongLyrics.mockResolvedValue({ synced: null, plain: null })
    mockSearchLyrics.mockResolvedValue([
      {
        id: 1, trackName: 'Intro', artistName: '', albumName: null,
        duration: null, syncedLyrics: '[00:01.00] hi', plainLyrics: null,
      },
    ])
    mockSaveLyrics.mockResolvedValue(undefined)
    mockEngineTime.mockReturnValue(10) // Intro (id 1) is active
    renderSheet()

    fireEvent.click(await screen.findByText('Search candidates'))
    // Picker defaults to the active chapter: saving closes the panel so
    // the fresh lyrics show instead of a bare "Saved" notice.
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(mockSaveLyrics).toHaveBeenCalledWith('ep.mp3', '[00:01.00] hi', null, 1)
    })
    await waitFor(() => {
      expect(screen.queryByLabelText('Save lyrics to chapter')).toBeNull()
    })
  })

  it('shows the saved lyrics even when the fetch keeps failing', async () => {
    // The fetch never succeeds (stale 404 / flaky network): the save seeds
    // the view cache, so the lyrics still show after jumping to them.
    mockSongLyrics.mockRejectedValue(new Error('Not found'))
    mockSearchLyrics.mockResolvedValue([
      {
        id: 1, trackName: 'Main', artistName: '', albumName: null,
        duration: null, syncedLyrics: '[00:01.00] saved words', plainLyrics: null,
      },
    ])
    mockSaveLyrics.mockResolvedValue(undefined)
    mockEngineTime.mockReturnValue(10) // Intro (id 1) is active
    renderSheet()

    expect(await screen.findByText('No lyrics saved for “Intro” yet.')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Search candidates'))
    const select = screen.getByLabelText('Save lyrics to chapter') as HTMLSelectElement
    fireEvent.change(select, { target: { value: '2' } })
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(mockSaveLyrics).toHaveBeenCalledWith('ep.mp3', '[00:01.00] saved words', null, 2)
    })
    // Jump to Main: the clock follows, the view reads the seeded cache —
    // no successful fetch ever happened.
    mockEngineTime.mockReturnValue(70)
    fireEvent.click(await screen.findByText('Jump to chapter'))
    expect(await screen.findByText('saved words')).toBeInTheDocument()
  })

  it('scopes the timing offset to the active chapter', async () => {
    // Lyrics only for chapter 1: 'hi' on screen proves the view (and the
    // offset hook beside it) resolved Intro, so the step below must carry
    // chapter id 1 — never the file scope.
    mockSongLyrics.mockImplementation(async (...args: unknown[]) => {
      const chapterId = args[5] as number | null | undefined
      return chapterId === 1 ? { synced: '[00:01.00] hi', plain: null } : { synced: null, plain: null }
    })
    mockEngineTime.mockReturnValue(10) // Intro (id 1) is active
    renderSheet()

    expect(await screen.findByText('hi')).toBeInTheDocument()
    fireEvent.click(screen.getByLabelText('Forward lyrics by 250 milliseconds'))
    await waitFor(() => {
      expect(mockSetOffset).toHaveBeenCalledWith('ep.mp3', 250, 1)
    })
  })
})
