import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import EditSongModal from './components/ui/EditSongModal'
import type { Song } from './api/client'

const { mockPatch, mockGet, mockPending, mockDiscard } = vi.hoisted(() => ({
  mockPatch: vi.fn(),
  mockGet: vi.fn(),
  mockPending: vi.fn(),
  mockDiscard: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      patchSong: mockPatch,
      song: mockGet,
      pendingEdits: mockPending,
      discardPendingEdit: mockDiscard,
    },
  }
})

const song: Song = {
  file: 's.mp3',
  artist: 'Old Artist',
  title: 'Old Title',
  album: 'Old Album',
  year: '2020',
  duration: 180,
  coverArt: null,
  dateDownload: null,
}

describe('EditSongModal submit', () => {
  it('sends the edited text values, not the originals', async () => {
    mockGet.mockResolvedValue({ ...song })
    mockPending.mockResolvedValue([])
    mockPatch.mockResolvedValue({ song: { ...song, title: 'New Title' }, resumeSec: 0 })
    const onSaved = vi.fn()
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <EditSongModal song={song} onClose={() => {}} onSaved={onSaved} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByPlaceholderText('Song Title'), { target: { value: 'New Title' } })
    fireEvent.change(screen.getByPlaceholderText('Artist Name'), { target: { value: 'New Artist' } })
    fireEvent.click(screen.getByText('Save Changes'))

    await waitFor(() =>
      expect(mockPatch).toHaveBeenCalledWith(
        's.mp3',
        expect.objectContaining({ title: 'New Title', artist: 'New Artist' }),
      ),
    )
    expect(onSaved).toHaveBeenCalled()
  })

  it('sends empty string (not null) when a field is cleared', async () => {
    mockGet.mockResolvedValue({ ...song })
    mockPending.mockResolvedValue([])
    mockPatch.mockResolvedValue({ song, resumeSec: 0 })
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <EditSongModal song={song} onClose={() => {}} onSaved={() => {}} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByPlaceholderText('Song Title'), { target: { value: '' } })
    fireEvent.click(screen.getByText('Save Changes'))

    await waitFor(() =>
      expect(mockPatch).toHaveBeenCalledWith('s.mp3', expect.objectContaining({ title: '' })),
    )
  })

  it('forwards the pending flag when the save is stashed for track change', async () => {
    mockGet.mockResolvedValue({ ...song })
    mockPending.mockResolvedValue([])
    mockPatch.mockResolvedValue({ song, resumeSec: 0, pending: true })
    const onSaved = vi.fn()
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <EditSongModal song={song} onClose={() => {}} onSaved={onSaved} />
      </QueryClientProvider>,
    )

    fireEvent.click(screen.getByText('Save Changes'))

    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(song, true))
  })

  it('prefills from a queued edit and discards it on request', async () => {
    mockGet.mockResolvedValue({ ...song })
    mockPending.mockResolvedValue([
      {
        file: 's.mp3',
        isPodcast: false,
        title: 'Queued Title',
        artist: null,
        album: null,
        year: null,
        genre: null,
        coverArt: null,
        createdUtc: '2026-10-07T00:00:00Z',
      },
    ])
    mockPatch.mockResolvedValue({ song, resumeSec: 0, pending: true })
    mockDiscard.mockResolvedValue(undefined)
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <EditSongModal song={song} onClose={() => {}} onSaved={() => {}} />
      </QueryClientProvider>,
    )

    expect(await screen.findByText(/Edit queued/)).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Song Title')).toHaveValue('Queued Title')

    fireEvent.click(screen.getByText('Discard'))
    await waitFor(() => expect(mockDiscard).toHaveBeenCalledWith('s.mp3'))
    await waitFor(() => expect(screen.queryByText(/Edit queued/)).toBeNull())
    expect(screen.getByPlaceholderText('Song Title')).toHaveValue('Old Title')
  })
})
