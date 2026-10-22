import { fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import Home from './views/Home'
import { usePlayer } from './store/player'
import type { Song } from './api/client'

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

const playedOne = song('rp1.mp3', 'Recent Played One')
const playedTwo = song('rp2.mp3', 'Recent Played Two')
const downloadedOne = song('rd1.mp3', 'Recent Download One')
const downloadedTwo = song('rd2.mp3', 'Recent Download Two')

const { mockRecentlyPlayed, mockRecentlyDownloaded } = vi.hoisted(() => ({
  mockRecentlyPlayed: vi.fn(),
  mockRecentlyDownloaded: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      songCount: async () => 4,
      recentlyPlayed: (...args: unknown[]) => mockRecentlyPlayed(...args),
      recentlyDownloaded: (...args: unknown[]) => mockRecentlyDownloaded(...args),
      dailyMix: async () => ({ date: '2026-10-21', songs: [], cached: true }),
    },
  }
})

const mockPlaySong = vi.fn(async () => {})
const originalPlaySong = usePlayer.getState().playSong

beforeEach(() => {
  vi.clearAllMocks()
  mockRecentlyPlayed.mockResolvedValue([playedOne, playedTwo])
  mockRecentlyDownloaded.mockResolvedValue([downloadedOne, downloadedTwo])
  usePlayer.setState({ playSong: mockPlaySong, currentSong: null, isPlaying: false })
})

afterEach(() => {
  usePlayer.setState({ playSong: originalPlaySong, currentSong: null, isPlaying: false })
})

function renderHome() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <Home />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('Home recent queues', () => {
  it('queues the Recently Played strip with its own source', async () => {
    renderHome()
    fireEvent.click(await screen.findByText('Recent Played One'))

    expect(mockPlaySong).toHaveBeenCalledWith(playedOne, [playedOne, playedTwo], {
      type: 'recently_played',
      id: null,
    })
  })

  it('queues the Recently Downloaded strip with its own source', async () => {
    renderHome()
    fireEvent.click(await screen.findByText('Recent Download Two'))

    expect(mockPlaySong).toHaveBeenCalledWith(
      downloadedTwo,
      [downloadedOne, downloadedTwo],
      { type: 'recently_downloaded', id: null },
    )
  })
})
