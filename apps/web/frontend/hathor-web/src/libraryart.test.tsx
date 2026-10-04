import { render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { AlbumDetail, ArtistDetail } from './views/Library'
import type { Song } from './api/client'

const { mockSongs, mockImage } = vi.hoisted(() => ({
  mockSongs: vi.fn(),
  mockImage: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      artistSongs: (...args: unknown[]) => mockSongs('artist', ...args),
      albumSongs: (...args: unknown[]) => mockSongs('album', ...args),
      artistImage: mockImage,
      albumImage: mockImage,
    },
  }
})

const song: Song = {
  file: 's.mp3',
  artist: 'Queen',
  title: 'Bohemian Rhapsody',
  album: 'A Night at the Opera',
  year: '1975',
  duration: 355,
  coverArt: null,
  dateDownload: null,
}

function renderAt(path: string, route: string, element: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path={route} element={element} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('ArtistDetail artwork', () => {
  it('shows the iTunes portrait when available', async () => {
    mockSongs.mockResolvedValue([song])
    mockImage.mockResolvedValue({ url: 'https://example.com/queen.jpg' })
    renderAt('/artists/Queen', '/artists/:name', <ArtistDetail />)
    const img = (await screen.findByAltText('Queen')) as HTMLImageElement
    expect(img.tagName).toBe('IMG')
    expect(img.src).toBe('https://example.com/queen.jpg')
    expect(mockImage).toHaveBeenCalledWith('Queen')
  })

  it('falls back when iTunes has nothing', async () => {
    mockSongs.mockResolvedValue([song])
    mockImage.mockResolvedValue(null)
    const { container } = renderAt('/artists/Queen', '/artists/:name', <ArtistDetail />)
    await screen.findByText('Queen')
    expect(container.querySelector('img')).toBeNull()
  })
})

describe('AlbumDetail artwork', () => {
  it('passes the disambiguating artist and shows the cover', async () => {
    mockSongs.mockResolvedValue([song])
    mockImage.mockResolvedValue({ url: 'https://example.com/opera.jpg' })
    renderAt('/albums/A%20Night%20at%20the%20Opera', '/albums/:title', <AlbumDetail />)
    const img = (await screen.findByAltText('A Night at the Opera')) as HTMLImageElement
    expect(img.src).toBe('https://example.com/opera.jpg')
    await waitFor(() => expect(mockImage).toHaveBeenCalledWith('A Night at the Opera', 'Queen'))
  })
})
