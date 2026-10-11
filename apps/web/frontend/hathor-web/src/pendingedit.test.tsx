import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PendingEditChip } from './components/PlayerBar'
import { usePlayer } from './store/player'
import type { Song } from './api/client'

const { mockPending, mockDiscard } = vi.hoisted(() => ({
  mockPending: vi.fn(),
  mockDiscard: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      pendingEdits: mockPending,
      discardPendingEdit: mockDiscard,
    },
  }
})

function renderChip(file: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <PendingEditChip file={file} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
})

describe('PendingEditChip', () => {
  it('shows for the playing file and discards on click', async () => {
    mockPending.mockResolvedValue([
      {
        file: 's.mp3',
        isPodcast: false,
        title: 'Queued',
        artist: null,
        album: null,
        year: null,
        genre: null,
        coverArt: null,
        createdUtc: '2026-10-07T00:00:00Z',
      },
    ])
    mockDiscard.mockResolvedValue(undefined)
    renderChip('s.mp3')

    expect(await screen.findByText('Edit queued')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Edit queued'))
    await waitFor(() => expect(mockDiscard).toHaveBeenCalledWith('s.mp3'))
  })

  it('stays hidden when nothing is stashed for the file', async () => {
    mockPending.mockResolvedValue([])
    const { container } = renderChip('s.mp3')
    await waitFor(() => expect(mockPending).toHaveBeenCalled())
    expect(container.firstChild).toBeNull()
  })

  it('stays hidden when the stash belongs to another file', async () => {
    mockPending.mockResolvedValue([
      {
        file: 'other.mp3',
        isPodcast: false,
        title: 'Queued',
        artist: null,
        album: null,
        year: null,
        genre: null,
        coverArt: null,
        createdUtc: '2026-10-07T00:00:00Z',
      },
    ])
    const { container } = renderChip('s.mp3')
    await waitFor(() => expect(mockPending).toHaveBeenCalled())
    expect(container.firstChild).toBeNull()
  })

  it('has a left margin', async () => {
    mockPending.mockResolvedValue([
      {
        file: 's.mp3',
        isPodcast: false,
        title: 'Queued',
        artist: null,
        album: null,
        year: null,
        genre: null,
        coverArt: null,
        createdUtc: '2026-10-07T00:00:00Z',
      },
    ])
    renderChip('s.mp3')
    expect(await screen.findByText('Edit queued')).toHaveClass('ml-1')
  })

  it('disappears once the edit applies on track change', async () => {
    // First fetch: row stashed for s.mp3. After the store moves on, the
    // chip refetches (server flushed it) and unmounts.
    mockPending
      .mockResolvedValueOnce([
        {
          file: 's.mp3',
          isPodcast: false,
          title: 'Queued',
          artist: null,
          album: null,
          year: null,
          genre: null,
          coverArt: null,
          createdUtc: '2026-10-07T00:00:00Z',
        },
      ])
      .mockResolvedValue([])
    const song = (file: string): Song => ({
      file,
      artist: 'Artist',
      title: file,
      album: '',
      year: '',
      duration: 180,
      coverArt: null,
      dateDownload: null,
    })
    usePlayer.setState({ currentSong: song('s.mp3'), isPlaying: true })
    const { container } = renderChip('s.mp3')
    expect(await screen.findByText('Edit queued')).toBeInTheDocument()

    usePlayer.setState({ currentSong: song('next.mp3'), isPlaying: true })
    await waitFor(() => expect(container.firstChild).toBeNull())
    expect(mockPending.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('disappears once the edit applies on queue-exhausted stop', async () => {
    // Same file, playing -> paused: the stop flushes the stash too.
    mockPending
      .mockResolvedValueOnce([
        {
          file: 's.mp3',
          isPodcast: false,
          title: 'Queued',
          artist: null,
          album: null,
          year: null,
          genre: null,
          coverArt: null,
          createdUtc: '2026-10-07T00:00:00Z',
        },
      ])
      .mockResolvedValue([])
    const song: Song = {
      file: 's.mp3',
      artist: 'Artist',
      title: 's.mp3',
      album: '',
      year: '',
      duration: 180,
      coverArt: null,
      dateDownload: null,
    }
    usePlayer.setState({ currentSong: song, isPlaying: true })
    const { container } = renderChip('s.mp3')
    expect(await screen.findByText('Edit queued')).toBeInTheDocument()

    usePlayer.setState({ isPlaying: false })
    await waitFor(() => expect(container.firstChild).toBeNull())
  })
})
