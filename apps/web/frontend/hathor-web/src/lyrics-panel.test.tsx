import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LyricsSheet from './components/LyricsSheet'
import { usePlayer } from './store/player'
import type { Song } from './api/client'

const { mockSearch, mockSave, mockDelete, mockGet, mockRefetch } = vi.hoisted(() => ({
  mockSearch: vi.fn(),
  mockSave: vi.fn(),
  mockDelete: vi.fn(),
  mockGet: vi.fn(),
  mockRefetch: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      songLyrics: mockGet,
      searchLyrics: mockSearch,
      saveLyrics: mockSave,
      deleteLyrics: mockDelete,
      romanize: mockRefetch,
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
}

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
  mockGet.mockResolvedValue({ synced: null, plain: null })
  mockSearch.mockResolvedValue([])
  mockSave.mockResolvedValue(undefined)
  mockDelete.mockResolvedValue(undefined)
})

describe('LyricsSheet search/replace', () => {
  it('shows Search when nothing found and searches with editable fields', async () => {
    renderSheet()
    expect(await screen.findByText('No lyrics found.')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Search candidates'))
    expect(await screen.findByPlaceholderText('Track name')).toBeInTheDocument()
    await waitFor(() => expect(mockSearch).toHaveBeenCalledWith('Title', 'Artist'))

    mockSearch.mockResolvedValue([
      {
        id: 7,
        trackName: 'Title',
        artistName: 'Artist',
        albumName: 'Album',
        duration: 180,
        syncedLyrics: '[00:01.00] la',
        plainLyrics: null,
      },
    ])
    // Header toggle + panel submit share the label — submit is the last one.
    const searchButtons = screen.getAllByText('Search')
    fireEvent.click(searchButtons[searchButtons.length - 1])
    expect(await screen.findByText('Title — Artist')).toBeInTheDocument()
  })

  it('previews a candidate and saves it as replacement', async () => {
    mockGet.mockResolvedValue({ synced: 'old', plain: null })
    mockSearch.mockResolvedValue([
      {
        id: 7,
        trackName: 'Title',
        artistName: 'Artist',
        albumName: null,
        duration: null,
        syncedLyrics: '[00:01.00] new words',
        plainLyrics: null,
      },
    ])
    renderSheet()
    fireEvent.click(await screen.findByText('Replace'))
    expect(await screen.findByText('Title — Artist')).toBeInTheDocument()

    fireEvent.click(screen.getByText('Preview'))
    expect(await screen.findByText('[00:01.00] new words')).toBeInTheDocument()

    // Header "Replace" toggles the panel; the row-level one saves.
    const replaceButtons = screen.getAllByText('Replace')
    expect(replaceButtons).toHaveLength(2)
    fireEvent.click(replaceButtons[1])
    await waitFor(() => expect(mockSave).toHaveBeenCalledWith('s.mp3', '[00:01.00] new words', null))
  })

  it('removes saved lyrics', async () => {
    mockGet.mockResolvedValue({ synced: 'old', plain: null })
    mockDelete.mockResolvedValue(undefined)
    renderSheet()
    fireEvent.click(await screen.findByText('Replace'))
    fireEvent.click(await screen.findByText('Remove saved'))
    await waitFor(() => expect(mockDelete).toHaveBeenCalledWith('s.mp3'))
  })
})
