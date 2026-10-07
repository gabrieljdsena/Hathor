import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PendingEditChip } from './components/PlayerBar'

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
})
