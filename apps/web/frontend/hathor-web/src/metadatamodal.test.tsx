import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import MetadataModal from './components/ui/MetadataModal'
import type { Song } from './api/client'

const { mockOptions, mockPatch } = vi.hoisted(() => ({
  mockOptions: vi.fn(),
  mockPatch: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: { ...mod.api, itunesOptions: mockOptions, patchSong: mockPatch },
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

const hit = {
  title: 'Hit Title',
  artist: 'Hit Artist',
  album: 'Hit Album',
  year: '2021',
  genre: 'Rock',
  artworkUrl: 'https://example.com/art.jpg',
}

function renderModal() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MetadataModal song={song} onClose={() => {}} />
    </QueryClientProvider>,
  )
}

describe('MetadataModal', () => {
  it('searches with the song metadata and lists candidates', async () => {
    mockOptions.mockResolvedValue([hit])
    renderModal()
    await waitFor(() => expect(mockOptions).toHaveBeenCalledWith('Title', 'Artist'))
    expect(await screen.findByText('Hit Title — Hit Artist')).toBeInTheDocument()
  })

  it('shows an empty state without candidates', async () => {
    mockOptions.mockResolvedValue([])
    renderModal()
    expect(await screen.findByText(/No candidates/)).toBeInTheDocument()
  })

  it('applies a candidate through patchSong, keeping missing fields', async () => {
    mockOptions.mockResolvedValue([{ ...hit, genre: '', artworkUrl: '' }])
    mockPatch.mockResolvedValue({ song, resumeSec: 0 })
    const onClose = vi.fn()
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={client}>
        <MetadataModal song={song} onClose={onClose} />
      </QueryClientProvider>,
    )
    expect(await screen.findByText('Hit Title — Hit Artist')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Apply'))
    await waitFor(() =>
      expect(mockPatch).toHaveBeenCalledWith(
        's.mp3',
        expect.objectContaining({
          title: 'Hit Title',
          artist: 'Hit Artist',
          genre: null,
          coverArt: null,
        }),
      ),
    )
    expect(onClose).toHaveBeenCalled()
  })
})
