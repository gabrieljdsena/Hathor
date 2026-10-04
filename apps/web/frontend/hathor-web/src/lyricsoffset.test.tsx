import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import SyncedLyrics from './components/ui/SyncedLyrics'
import LyricsSheet from './components/LyricsSheet'
import { usePlayer } from './store/player'
import type { Song } from './api/client'

const { mockGetOffset, mockSetOffset } = vi.hoisted(() => ({
  mockGetOffset: vi.fn(),
  mockSetOffset: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      songLyrics: async () => ({ synced: null, plain: null }),
      searchLyrics: async () => [],
      lyricsOffset: mockGetOffset,
      setLyricsOffset: mockSetOffset,
    },
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
  isPodcast: false,
}

const synced = ['[00:05.00] first', '[00:10.00] second'].join('\n')

function renderSheet() {
  usePlayer.setState({ currentSong: song })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <LyricsSheet open onClose={() => {}} />
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  window.HTMLElement.prototype.scrollTo = vi.fn() as unknown as typeof window.HTMLElement.prototype.scrollTo
  mockGetOffset.mockResolvedValue({ offsetMs: 0 })
  mockSetOffset.mockImplementation(async (_f: string, ms: number) => ({ offsetMs: ms }))
})

describe('SyncedLyrics timing offset', () => {
  it('shifts the highlighted line by the offset prop', () => {
    // Engine time is 0 in jsdom: no line highlighted yet.
    render(<SyncedLyrics synced={synced} plain={null} isLoading={false} offsetMs={6000} />)
    expect(screen.getByText('first').className).toContain('text-white')
    expect(screen.getByText('second').className).not.toContain('text-white')
  })
})

describe('LyricsSheet offset control', () => {
  it('steps ±250ms and persists through the API', async () => {
    renderSheet()
    const input = (await screen.findByLabelText(
      'Lyrics timing offset in milliseconds',
    )) as HTMLInputElement
    expect(input.value).toBe('0')

    fireEvent.click(screen.getByLabelText('Forward lyrics by 250 milliseconds'))
    await waitFor(() => expect(mockSetOffset).toHaveBeenCalledWith('s.mp3', 250))

    fireEvent.click(screen.getByLabelText('Retard lyrics by 250 milliseconds'))
    await waitFor(() => expect(mockSetOffset).toHaveBeenCalledWith('s.mp3', 0))
  })

  it('commits typed values on Enter', async () => {
    renderSheet()
    const input = (await screen.findByLabelText(
      'Lyrics timing offset in milliseconds',
    )) as HTMLInputElement
    fireEvent.change(input, { target: { value: '1000' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    await waitFor(() => expect(mockSetOffset).toHaveBeenCalledWith('s.mp3', 1000))
  })
})
