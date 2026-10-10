import { fireEvent, render, renderHook, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ChapterSkip,
  TimestampManagerModal,
  useChapterAutoSkip,
  useChapterJump,
} from './components/ui/PodcastTimestamps'
import { usePlayer } from './store/player'
import type { PodcastTimestamp, Song } from './api/client'

const { mockList, mockCreate, mockUpdate, mockRemove, mockSeek, mockEngineTime, mockEngineSeek, mockEngineFile, mockUpdateSettings } =
  vi.hoisted(() => ({
    mockList: vi.fn(),
    mockCreate: vi.fn(),
    mockUpdate: vi.fn(),
    mockRemove: vi.fn(),
    mockSeek: vi.fn(),
    mockEngineTime: vi.fn(),
    mockEngineSeek: vi.fn(),
    mockEngineFile: vi.fn(),
    mockUpdateSettings: vi.fn(),
  }))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      podcastTimestamps: (...args: unknown[]) => mockList(...args),
      createPodcastTimestamp: (...args: unknown[]) => mockCreate(...args),
      updatePodcastTimestamp: (...args: unknown[]) => mockUpdate(...args),
      deletePodcastTimestamp: (...args: unknown[]) => mockRemove(...args),
      seek: (...args: unknown[]) => mockSeek(...args),
      updateSettings: (...args: unknown[]) => mockUpdateSettings(...args),
    },
  }
})

vi.mock('./audio/engine', () => ({
  engine: {
    time: (...args: unknown[]) => mockEngineTime(...args),
    seek: (...args: unknown[]) => mockEngineSeek(...args),
    currentFile: (...args: unknown[]) => mockEngineFile(...args),
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

function client() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
}

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

function resetPlayer() {
  usePlayer.setState({
    currentSong: null,
    isPlaying: false,
    chapterSkip: false,
    queue: [],
    queueTotal: 0,
  })
  localStorage.clear()
}

beforeEach(() => {
  resetPlayer()
  vi.clearAllMocks()
  // Element renders the store's episode unless a test says otherwise.
  mockEngineFile.mockReturnValue('ep.mp3')
})

function renderModal() {
  return render(
    <QueryClientProvider client={client()}>
      <TimestampManagerModal file="ep.mp3" episodeTitle="Episode" onClose={() => {}} />
    </QueryClientProvider>,
  )
}

describe('TimestampManagerModal', () => {
  it('lists chapters and adds one with parsed times', async () => {
    mockList.mockResolvedValue(chapters)
    mockCreate.mockImplementation((_f: unknown, input: unknown) =>
      Promise.resolve({ id: 3, podcastFile: 'ep.mp3', ...(input as object) }),
    )
    renderModal()

    expect(await screen.findByText('Intro')).toBeInTheDocument()
    expect(screen.getByText('Main')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Chapter name'), { target: { value: 'Outro' } })
    fireEvent.change(screen.getByLabelText('Starts at'), { target: { value: '10:00' } })
    fireEvent.change(screen.getByLabelText('Ends at (optional)'), { target: { value: '10:30' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add chapter' }))

    await waitFor(() =>
      expect(mockCreate).toHaveBeenCalledWith('ep.mp3', {
        name: 'Outro',
        startSecs: 600,
        endSecs: 630,
      }),
    )
  })

  it('shows a validation error for an end before the start', async () => {
    mockList.mockResolvedValue(chapters)
    renderModal()
    await screen.findByText('Intro')

    fireEvent.change(screen.getByLabelText('Chapter name'), { target: { value: 'Bad' } })
    fireEvent.change(screen.getByLabelText('Starts at'), { target: { value: '1:00' } })
    fireEvent.change(screen.getByLabelText('Ends at (optional)'), { target: { value: '0:30' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add chapter' }))

    expect(await screen.findByText(/later than the start/)).toBeInTheDocument()
    expect(mockCreate).not.toHaveBeenCalled()
  })

  it('edits and deletes with a two-tap confirm', async () => {
    mockList.mockResolvedValue(chapters)
    mockUpdate.mockImplementation((_f: unknown, _id: unknown, input: unknown) =>
      Promise.resolve({ id: 1, podcastFile: 'ep.mp3', ...(input as object) }),
    )
    mockRemove.mockResolvedValue(undefined)
    renderModal()
    await screen.findByText('Intro')

    const row = screen.getByText('Intro').closest('div[class*="grid"]') as HTMLElement
    fireEvent.click(within(row).getByTitle('Edit chapter'))
    fireEvent.change(screen.getByLabelText('Chapter name'), { target: { value: 'Cold open' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(mockUpdate).toHaveBeenCalledWith(
        'ep.mp3',
        1,
        expect.objectContaining({ name: 'Cold open' }),
      ),
    )

    const mainRow = screen.getByText('Main').closest('div[class*="grid"]') as HTMLElement
    fireEvent.click(within(mainRow).getByTitle('Delete chapter'))
    fireEvent.click(within(mainRow).getByTitle('Confirm delete'))
    await waitFor(() => expect(mockRemove).toHaveBeenCalledWith('ep.mp3', 2))
  })
})

describe('ChapterSkip', () => {
  it('highlights the active chapter and seeks on click', async () => {
    mockList.mockResolvedValue(chapters)
    mockEngineTime.mockReturnValue(61)
    mockSeek.mockResolvedValue({})
    render(
      <QueryClientProvider client={client()}>
        <ChapterSkip file="ep.mp3" />
      </QueryClientProvider>,
    )

    const main = await screen.findByTitle(/Skip to Main/);
    expect(main.className).toMatch(/orange/)
    fireEvent.click(await screen.findByTitle(/Skip to Intro/))
    expect(mockEngineSeek).toHaveBeenCalledWith(0)
    expect(mockSeek).toHaveBeenCalledWith(0)
  })

  it('renders nothing without chapters', async () => {
    mockList.mockResolvedValue([])
    const { container } = render(
      <QueryClientProvider client={client()}>
        <ChapterSkip file="empty.mp3" />
      </QueryClientProvider>,
    )
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('empty.mp3'))
    expect(container.firstChild).toBeNull()
  })

  it('toggles auto-skip in the store and persists it account-wide', async () => {
    mockList.mockResolvedValue(chapters)
    mockUpdateSettings.mockResolvedValue({})
    render(
      <QueryClientProvider client={client()}>
        <ChapterSkip file="ep.mp3" />
      </QueryClientProvider>,
    )
    const toggle = await screen.findByRole('switch', { name: /auto-skip/i })
    expect(toggle).toHaveAttribute('aria-checked', 'false')
    fireEvent.click(toggle)
    expect(usePlayer.getState().chapterSkip).toBe(true)
    expect(localStorage.getItem('hathor:chapterskip')).toBe('1')
    expect(mockUpdateSettings).toHaveBeenCalledWith({ chapterSkip: true })
    expect(toggle).toHaveAttribute('aria-checked', 'true')
  })
})

describe('useChapterJump', () => {
  function hookWrapper() {
    const qc = client()
    return function Wrapper({ children }: { children: React.ReactNode }) {
      return <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    }
  }

  it('jumps chapter boundaries for podcasts even with auto-skip off', async () => {
    mockList.mockResolvedValue(chapters)
    mockEngineTime.mockReturnValue(10)
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode })
    const { result } = renderHook(() => useChapterJump(), { wrapper: hookWrapper() })
    await waitFor(() => expect(result.current.next()).toBe(true))
    expect(mockEngineSeek).toHaveBeenCalledWith(60)

    mockEngineTime.mockReturnValue(100)
    expect(result.current.prev()).toBe(true)
    expect(mockEngineSeek).toHaveBeenCalledWith(60)
  })

  it('falls through to track transport past the last chapter', async () => {
    mockList.mockResolvedValue(chapters)
    mockEngineTime.mockReturnValue(5000)
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode })
    const { result } = renderHook(() => useChapterJump(), { wrapper: hookWrapper() })
    await waitFor(() => expect(result.current.prev()).toBe(true))
    expect(result.current.next()).toBe(false)
  })

  it('never chapter-seeks a song the store still mistakes for a podcast', async () => {
    // Store/engine divergence (switch mid-flight, failed play call): the
    // store names the old episode with chapters while the element already
    // plays a song. Transport must fall through to track change instead of
    // seeking the song to the old episode's chapter point (which leaves the
    // queue looking frozen or wrong).
    mockList.mockResolvedValue(chapters)
    mockEngineTime.mockReturnValue(10)
    mockEngineFile.mockReturnValue('song.mp3')
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode })
    const { result } = renderHook(() => useChapterJump(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('ep.mp3'))
    expect(result.current.next()).toBe(false)
    expect(result.current.prev()).toBe(false)
    expect(mockEngineSeek).not.toHaveBeenCalled()
    expect(mockSeek).not.toHaveBeenCalled()
  })

  it('chapter-jumps again once the element catches up to the store episode', async () => {
    mockList.mockResolvedValue(chapters)
    mockEngineTime.mockReturnValue(10)
    mockEngineFile.mockReturnValue('song.mp3')
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode })
    const { result } = renderHook(() => useChapterJump(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('ep.mp3'))
    expect(result.current.next()).toBe(false)
    // Server confirms: the element now renders the store's episode.
    mockEngineFile.mockReturnValue('ep.mp3')
    await waitFor(() => expect(result.current.next()).toBe(true))
    expect(mockEngineSeek).toHaveBeenCalledWith(60)
  })

  it('stays on track transport for songs and chapterless episodes', async () => {
    mockList.mockResolvedValue([])
    usePlayer.setState({ currentSong: { ...episode, isPodcast: false } })
    const { result } = renderHook(() => useChapterJump(), { wrapper: hookWrapper() })
    // Non-podcast: the chapter query stays disabled.
    await new Promise((r) => setTimeout(r, 150))
    expect(mockList).not.toHaveBeenCalled()
    // Both arrows stay on tracks.
    expect(result.current.next()).toBe(false)
    expect(result.current.prev()).toBe(false)
  })
})

describe('useChapterAutoSkip', () => {
  function hookWrapper() {
    const qc = client()
    return function Wrapper({ children }: { children: React.ReactNode }) {
      return <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    }
  }

  const skipChapters: PodcastTimestamp[] = [
    { id: 1, podcastFile: 'ep.mp3', name: 'Intro', startSecs: 0, endSecs: null },
    { id: 2, podcastFile: 'ep.mp3', name: 'Ad', startSecs: 60, endSecs: 90 },
    { id: 3, podcastFile: 'ep.mp3', name: 'Main', startSecs: 120, endSecs: null },
  ]

  it('starts from the first chapter instead of 0:00 when enabled', async () => {
    mockList.mockResolvedValue(skipChapters)
    // skipChapters opens with Intro at 0 — shift it so there is a lead-in.
    mockList.mockResolvedValueOnce([
      { id: 1, podcastFile: 'ep.mp3', name: 'Intro', startSecs: 45, endSecs: null },
      { id: 2, podcastFile: 'ep.mp3', name: 'Main', startSecs: 120, endSecs: null },
    ])
    mockEngineTime.mockReturnValue(2)
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode, isPlaying: true, chapterSkip: true })
    renderHook(() => useChapterAutoSkip(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockEngineSeek).toHaveBeenCalledWith(45), { timeout: 3000 })
    expect(mockSeek).toHaveBeenCalledWith(45)
  })

  it('jumps the gap into the next start once the end time plays', async () => {
    mockList.mockResolvedValue(skipChapters)
    mockEngineTime.mockReturnValue(95)
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode, isPlaying: true, chapterSkip: true })
    renderHook(() => useChapterAutoSkip(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockEngineSeek).toHaveBeenCalledWith(120), { timeout: 3000 })
    expect(mockSeek).toHaveBeenCalledWith(120)
  })

  it('stays put mid-segment and when the feature is off', async () => {
    mockList.mockResolvedValue(skipChapters)
    // Inside the Ad segment but before its end: plays on, no skip.
    mockEngineTime.mockReturnValue(70)
    usePlayer.setState({ currentSong: episode, isPlaying: true, chapterSkip: true })
    renderHook(() => useChapterAutoSkip(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('ep.mp3'))
    await new Promise((r) => setTimeout(r, 700))
    expect(mockEngineSeek).not.toHaveBeenCalled()
  })

  it('stays put when the feature is off, even past an end time', async () => {
    mockList.mockResolvedValue(skipChapters)
    mockEngineTime.mockReturnValue(95)
    usePlayer.setState({ currentSong: episode, isPlaying: true, chapterSkip: false })
    renderHook(() => useChapterAutoSkip(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('ep.mp3'))
    await new Promise((r) => setTimeout(r, 700))
    expect(mockEngineSeek).not.toHaveBeenCalled()
  })

  it('ignores open-ended markers', async () => {
    mockList.mockResolvedValue(skipChapters)
    mockEngineTime.mockReturnValue(10)
    usePlayer.setState({ currentSong: episode, isPlaying: true, chapterSkip: true })
    renderHook(() => useChapterAutoSkip(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('ep.mp3'))
    await new Promise((r) => setTimeout(r, 700))
    expect(mockEngineSeek).not.toHaveBeenCalled()
  })

  it('never seeks a song the store still mistakes for a podcast', async () => {
    // Store/engine divergence (switch mid-flight, failed play call): the
    // store names the old episode with chapters while the element already
    // plays a song. The pump must not fire chapter timestamps at it.
    mockList.mockResolvedValue(skipChapters)
    mockEngineTime.mockReturnValue(95)
    mockEngineFile.mockReturnValue('song.mp3')
    mockSeek.mockResolvedValue({})
    usePlayer.setState({ currentSong: episode, isPlaying: true, chapterSkip: true })
    renderHook(() => useChapterAutoSkip(), { wrapper: hookWrapper() })
    await waitFor(() => expect(mockList).toHaveBeenCalledWith('ep.mp3'))
    await new Promise((r) => setTimeout(r, 700))
    expect(mockEngineSeek).not.toHaveBeenCalled()
    expect(mockSeek).not.toHaveBeenCalled()
  })
})
