import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Albums, Artists } from './views/Library'

const { mockArtists, mockAlbums } = vi.hoisted(() => ({
  mockArtists: vi.fn(),
  mockAlbums: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: { ...mod.api, artists: mockArtists, albums: mockAlbums },
  }
})

function renderList(element: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{element}</MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  mockArtists.mockResolvedValue(['Queen', 'Queens of the Stone Age', 'ABBA'])
  mockAlbums.mockResolvedValue(['A Night at the Opera', 'News of the World', 'ABBA Gold'])
})

describe('Artists search', () => {
  it('filters the list and shows a no-match state', async () => {
    renderList(<Artists />)
    await screen.findByText('ABBA')

    fireEvent.change(screen.getByPlaceholderText('Search artists...'), { target: { value: 'queen' } })
    await waitFor(() => expect(screen.queryByText('ABBA')).toBeNull())
    expect(screen.getByText('Queen')).toBeInTheDocument()
    expect(screen.getByText('Queens of the Stone Age')).toBeInTheDocument()

    fireEvent.change(screen.getByPlaceholderText('Search artists...'), { target: { value: 'xyz-nope' } })
    await waitFor(() => expect(screen.getByText('No artists match')).toBeInTheDocument())

    fireEvent.click(screen.getByLabelText('Clear search'))
    await waitFor(() => expect(screen.getByText('ABBA')).toBeInTheDocument())
  })
})

describe('Albums search', () => {
  it('filters the list and shows a no-match state', async () => {
    renderList(<Albums />)
    await screen.findByText('ABBA Gold')

    fireEvent.change(screen.getByPlaceholderText('Search albums...'), { target: { value: 'world' } })
    await waitFor(() => expect(screen.queryByText('ABBA Gold')).toBeNull())
    expect(screen.getByText('News of the World')).toBeInTheDocument()

    fireEvent.change(screen.getByPlaceholderText('Search albums...'), { target: { value: 'xyz-nope' } })
    await waitFor(() => expect(screen.getByText('No albums match')).toBeInTheDocument())
  })
})
