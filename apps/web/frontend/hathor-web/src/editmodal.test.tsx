import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import EditSongModal from './components/ui/EditSongModal'
import type { Song } from './api/client'

const { mockPatch, mockGet } = vi.hoisted(() => ({
  mockPatch: vi.fn(),
  mockGet: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      patchSong: mockPatch,
      song: mockGet,
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
})
