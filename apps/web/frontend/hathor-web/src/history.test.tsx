import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import History from './views/History'
import { usePlayer } from './store/player'
import type { HistoryItem, Song } from './api/client'

function song(file: string, title: string): Song {
  return {
    file,
    artist: 'Artist',
    title,
    album: 'Album',
    year: '2020',
    duration: 180,
    coverArt: null,
    dateDownload: null,
  }
}

const downloadOne = song('d1.mp3', 'Download One')
const downloadTwo = song('d2.mp3', 'Download Two')
const playedOne = song('p1.mp3', 'Played One')
const playedTwo = song('p2.mp3', 'Played Two')

function item(s: Song, dateDownload: string | null, datePlayed: string | null): HistoryItem {
  return { song: s, dateDownload, datePlayed, downloadedLink: dateDownload ? 'https://source' : null }
}

const downloadPage = {
  items: [
    item(downloadOne, '2026-10-20T10:00:00Z', null),
    item(downloadTwo, '2026-10-19T10:00:00Z', null),
  ],
  totalPages: 1,
  currentPage: 1,
}
const playedPage = {
  items: [
    item(playedOne, null, '2026-10-20T10:00:00Z'),
    item(playedTwo, null, '2026-10-19T10:00:00Z'),
  ],
  totalPages: 1,
  currentPage: 1,
}

const {
  mockDownloadHistory,
  mockDownloadFiles,
  mockPlayedHistory,
  mockPlayedFiles,
} = vi.hoisted(() => ({
  mockDownloadHistory: vi.fn(),
  mockDownloadFiles: vi.fn(),
  mockPlayedHistory: vi.fn(),
  mockPlayedFiles: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      downloadHistory: (...args: unknown[]) => mockDownloadHistory(...args),
      downloadHistoryFiles: (...args: unknown[]) => mockDownloadFiles(...args),
      playedHistory: (...args: unknown[]) => mockPlayedHistory(...args),
      playedHistoryFiles: (...args: unknown[]) => mockPlayedFiles(...args),
    },
  }
})

const mockPlaySongByFiles = vi.fn(async () => {})
const originalPlaySongByFiles = usePlayer.getState().playSongByFiles

function renderHistory(client: QueryClient) {
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <History />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  mockDownloadHistory.mockResolvedValue(downloadPage)
  mockPlayedHistory.mockResolvedValue(playedPage)
  mockDownloadFiles.mockResolvedValue([])
  mockPlayedFiles.mockResolvedValue([])
  usePlayer.setState({
    playSongByFiles: mockPlaySongByFiles,
    currentSong: null,
    isPlaying: false,
  })
})

afterEach(() => {
  usePlayer.setState({
    playSongByFiles: originalPlaySongByFiles,
    currentSong: null,
    isPlaying: false,
  })
})

describe('History tab queues', () => {
  it('plays downloads with the full download-history source', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(['history-download-files'], ['d3.mp3', 'd1.mp3', 'd2.mp3'])
    renderHistory(client)

    fireEvent.click(await screen.findByText('Download Two'))

    expect(mockPlaySongByFiles).toHaveBeenCalledWith(
      downloadTwo,
      ['d3.mp3', 'd1.mp3', 'd2.mp3'],
      { type: 'download_history', id: null },
    )
  })

  it('plays played history with the deduped played source', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    client.setQueryData(['history-played-files'], ['p2.mp3', 'p1.mp3'])
    renderHistory(client)

    fireEvent.click(await screen.findByText('Played History'))
    fireEvent.click(await screen.findByText('Played One'))

    expect(mockPlaySongByFiles).toHaveBeenCalledWith(
      playedOne,
      ['p2.mp3', 'p1.mp3'],
      { type: 'recently_played', id: null },
    )
  })

  it('falls back to the visible page while the full list is loading', async () => {
    mockDownloadFiles.mockReturnValue(new Promise(() => {}))
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    renderHistory(client)

    fireEvent.click(await screen.findByText('Download One'))

    await waitFor(() =>
      expect(mockPlaySongByFiles).toHaveBeenCalledWith(
        downloadOne,
        ['d1.mp3', 'd2.mp3'],
        { type: 'download_history', id: null },
      ),
    )
  })
})
