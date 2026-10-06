import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import LibraryTable from './components/LibraryTable'
import StripCard from './components/ui/StripCard'
import Podcasts from './views/Podcasts'
import { claimMenu, releaseMenu, subscribeMenu } from './components/ui/menuBus'
import type { Song } from './api/client'

const mockEpisode: Song = {
  file: 'ep1.mp3',
  artist: 'Host',
  title: 'Episode',
  album: '',
  year: '',
  duration: 60,
  coverArt: null,
  dateDownload: null,
  isPodcast: true,
}

const { mockMoveSongToPodcasts, mockMovePodcastToSongs } = vi.hoisted(() => ({
  mockMoveSongToPodcasts: vi.fn(),
  mockMovePodcastToSongs: vi.fn(),
}))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: {
      ...mod.api,
      podcasts: async () => [mockEpisode],
      podcastTags: async () => [],
      podcastTagMap: async () => ({}),
      moveSongToPodcasts: (...args: unknown[]) => mockMoveSongToPodcasts(...args),
      movePodcastToSongs: (...args: unknown[]) => mockMovePodcastToSongs(...args),
    },
  }
})

const songs: Song[] = [
  {
    file: 's.mp3',
    artist: 'Artist',
    title: 'Title',
    album: 'Album',
    year: '2020',
    duration: 180,
    coverArt: null,
    dateDownload: null,
  },
]

function renderTable() {
  const client = new QueryClient()
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <LibraryTable songs={songs} isLoading={false} source={{ type: 'all_songs', id: null }} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('LibraryTable context menu', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('opens the song menu at the cursor on right-click', () => {
    renderTable()
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!, { clientX: 100, clientY: 200 })
    expect(screen.getByText('Play Next')).toBeInTheDocument()
    const menu = screen.getByText('Play Next').closest('div.fixed') as HTMLElement | null
    expect(menu).not.toBeNull()
    expect(menu!.style.left).toBe('100px')
    expect(menu!.style.top).toBe('200px')
  })

  it('cursor menu portals to body above sheets but below modals', () => {
    renderTable()
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!, { clientX: 100, clientY: 200 })
    const menu = screen.getByText('Play Next').closest('div.fixed') as HTMLElement | null
    expect(menu).not.toBeNull()
    expect(menu!.parentElement).toBe(document.body)
    expect(menu!.className).toContain('z-[90]')
  })

  it('button menu portals to body at z-[90]', () => {
    renderTable()
    fireEvent.click(screen.getAllByTitle('More actions')[0])
    expect(screen.getByText('Play Next')).toBeInTheDocument()
    const menu = screen.getByText('Play Next').closest('div.fixed') as HTMLElement | null
    expect(menu).not.toBeNull()
    expect(menu!.parentElement).toBe(document.body)
    expect(menu!.className).toContain('z-[90]')
  })

  it('does not open the menu on left-click (left-click plays)', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    )
    renderTable()
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.click(row!, { clientX: 150, clientY: 250, detail: 1 })
    expect(screen.queryByText('Play Next')).not.toBeInTheDocument()
  })
})

describe('StripCard context menu', () => {
  it('opens the song menu at the cursor on right-click', () => {
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <StripCard song={songs[0]} onPlay={() => {}} menuSourceType="recently_played" />
      </QueryClientProvider>,
    )
    const card = screen.getByText('Title').closest('div.group')
    expect(card).not.toBeNull()
    fireEvent.contextMenu(card!, { clientX: 120, clientY: 220 })
    expect(screen.getByText('Play Next')).toBeInTheDocument()
    const menu = screen.getByText('Play Next').closest('div.fixed') as HTMLElement | null
    expect(menu).not.toBeNull()
    expect(menu!.style.left).toBe('120px')
    expect(menu!.style.top).toBe('220px')
  })

  it('has no menu without menuSourceType', () => {
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <StripCard song={songs[0]} onPlay={() => {}} />
      </QueryClientProvider>,
    )
    const card = screen.getByText('Title').closest('div.group')
    expect(card).not.toBeNull()
    fireEvent.contextMenu(card!)
    expect(screen.queryByText('Play Next')).not.toBeInTheDocument()
  })
})

describe('menu bus (single open menu)', () => {
  it('a new claim closes the previous menu', () => {
    const seen: Array<string | null> = []
    const unsub = subscribeMenu((id) => seen.push(id))
    claimMenu('a')
    claimMenu('b')
    releaseMenu('b')
    unsub()
    expect(seen).toEqual(['a', 'b', null])
  })

  it('opening a second row menu closes the first', () => {
    const client = new QueryClient()
    const two: Song[] = [
      { ...songs[0], file: 'a.mp3' },
      { ...songs[0], file: 'b.mp3', title: 'Other' },
    ]
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <LibraryTable songs={two} isLoading={false} source={{ type: 'all_songs', id: null }} />
        </MemoryRouter>
      </QueryClientProvider>,
    )
    const first = screen.getByText('Title').closest('div.grid')
    const second = screen.getByText('Other').closest('div.grid')
    expect(first).not.toBeNull()
    expect(second).not.toBeNull()
    fireEvent.contextMenu(first!, { clientX: 100, clientY: 200 })
    expect(screen.getAllByText('Play Next')).toHaveLength(1)
    fireEvent.contextMenu(second!, { clientX: 300, clientY: 400 })
    const menus = screen.getAllByText('Play Next')
    expect(menus).toHaveLength(1)
    const menu = menus[0].closest('div.fixed') as HTMLElement | null
    expect(menu!.style.left).toBe('300px')
    expect(menu!.style.top).toBe('400px')
  })
})

describe('Podcasts episode menu', () => {
  function renderPodcasts() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={client}>
        <Podcasts />
      </QueryClientProvider>,
    )
  }

  it('opens on bento click and stays open (no self-close race)', async () => {
    const { container } = renderPodcasts()
    expect(await screen.findByText('Episode')).toBeInTheDocument()
    const buttons = container.querySelectorAll('button[title="More actions"]')
    expect(buttons).toHaveLength(1)
    fireEvent.click(buttons[0])
    // The bus claim must not instantly close the menu we just opened.
    expect(await screen.findByText('Get Metadata')).toBeInTheDocument()
  })

  it('opens on right-click', async () => {
    renderPodcasts()
    expect(await screen.findByText('Episode')).toBeInTheDocument()
    const row = screen.getByText('Episode').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!)
    expect(await screen.findByText('Get Metadata')).toBeInTheDocument()
  })

  it('moves the episode to songs behind a confirmation', async () => {
    mockMovePodcastToSongs.mockResolvedValue({ ...mockEpisode, isPodcast: false })
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    )
    renderPodcasts()
    expect(await screen.findByText('Episode')).toBeInTheDocument()
    const row = screen.getByText('Episode').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!)
    fireEvent.click(await screen.findByText('Move to Songs…'))
    expect(await screen.findByText('Move to Songs?')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Yes, move it' }))
    await waitFor(() => expect(mockMovePodcastToSongs).toHaveBeenCalledWith('ep1.mp3'))
  })

  it('episode menu has no Move to Podcasts entry', async () => {
    renderPodcasts()
    expect(await screen.findByText('Episode')).toBeInTheDocument()
    const row = screen.getByText('Episode').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!)
    expect(await screen.findByText('Move to Songs…')).toBeInTheDocument()
    expect(screen.queryByText('Move to Podcasts…')).not.toBeInTheDocument()
  })
})

describe('Move between libraries (song menu)', () => {
  it('song menu moves to podcasts behind a confirmation', async () => {
    mockMoveSongToPodcasts.mockResolvedValue({ ...songs[0], isPodcast: true })
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    )
    renderTable()
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!, { clientX: 100, clientY: 200 })
    fireEvent.click(await screen.findByText('Move to Podcasts…'))
    expect(await screen.findByText('Move to Podcasts?')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Yes, move it' }))
    await waitFor(() => expect(mockMoveSongToPodcasts).toHaveBeenCalledWith('s.mp3'))
  })

  it('song menu has no Move to Songs entry', () => {
    renderTable()
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!, { clientX: 100, clientY: 200 })
    expect(screen.queryByText('Move to Songs…')).not.toBeInTheDocument()
  })
})

describe('Remove from playlist', () => {
  function renderPlaylistTable(onRemove?: (song: Song) => void) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <LibraryTable
            songs={songs}
            isLoading={false}
            source={{ type: 'playlist', id: '7' }}
            onRemoveFromPlaylist={onRemove}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    )
  }

  it('shows a per-row remove button that calls back with the song', () => {
    const onRemove = vi.fn()
    renderPlaylistTable(onRemove)
    const btn = screen.getByTitle('Remove from playlist')
    fireEvent.click(btn)
    expect(onRemove).toHaveBeenCalledTimes(1)
    expect(onRemove.mock.calls[0][0].file).toBe('s.mp3')
  })

  it('menu carries Remove from playlist in playlist context only', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 204 })),
    )
    renderPlaylistTable()
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!, { clientX: 100, clientY: 200 })
    fireEvent.click(await screen.findByText('Remove from playlist'))
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledWith(
        expect.stringContaining('/playlists/7/songs/s.mp3'),
        expect.objectContaining({ method: 'DELETE' }),
      )
    })
  })

  it('menu has no remove item outside playlists', () => {
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <LibraryTable songs={songs} isLoading={false} source={{ type: 'all_songs', id: null }} />
        </MemoryRouter>
      </QueryClientProvider>,
    )
    const row = screen.getByText('Title').closest('div.grid')
    expect(row).not.toBeNull()
    fireEvent.contextMenu(row!, { clientX: 100, clientY: 200 })
    expect(screen.queryByText('Remove from playlist')).not.toBeInTheDocument()
  })
})

describe('LibraryTable Added column', () => {
  const dated: Song[] = [
    { ...songs[0], file: 'new.mp3', title: 'New Song', dateDownload: '2026-10-04T12:00:00Z' },
    { ...songs[0], file: 'old.mp3', title: 'Old Song', dateDownload: '2024-05-01T12:00:00Z' },
  ]

  function renderDated() {
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <LibraryTable songs={dated} isLoading={false} source={{ type: 'all_songs', id: null }} />
        </MemoryRouter>
      </QueryClientProvider>,
    )
  }

  function visibleTitles() {
    return screen
      .getAllByText(/^(New Song|Old Song)$/)
      .map((el) => el.textContent)
  }

  it('shows the Added header and sorts oldest-first on click', () => {
    renderDated()
    expect(visibleTitles()).toEqual(['New Song', 'Old Song'])
    fireEvent.click(screen.getByText('Added'))
    expect(visibleTitles()).toEqual(['Old Song', 'New Song'])
  })

  it('toggles to newest-first on second click', () => {
    renderDated()
    const header = screen.getByText('Added')
    fireEvent.click(header)
    fireEvent.click(header)
    expect(visibleTitles()).toEqual(['New Song', 'Old Song'])
  })
})
