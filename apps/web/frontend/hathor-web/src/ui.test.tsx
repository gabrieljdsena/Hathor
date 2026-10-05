import { fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import NowPlaying from './components/NowPlaying'
import PlayerBar from './components/PlayerBar'
import SearchInput from './components/ui/SearchInput'
import SongRow, { VisualizerBars } from './components/ui/SongRow'
import SortableHeader from './components/ui/SortableHeader'
import ToggleSwitch from './components/ui/ToggleSwitch'
import { TagPills } from './components/ui/PodcastTags'
import type { Song } from './api/client'

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

describe('SearchInput', () => {
  it('shows a clear button that resets the value', () => {
    let value = 'query'
    const onChange = (v: string) => {
      value = v
    }
    const { rerender } = render(<SearchInput value={value} onChange={onChange} />)
    expect(screen.getByLabelText('Clear search')).toBeInTheDocument()
    fireEvent.click(screen.getByLabelText('Clear search'))
    expect(value).toBe('')
    rerender(<SearchInput value={value} onChange={onChange} />)
    expect(screen.queryByLabelText('Clear search')).not.toBeInTheDocument()
  })
})

describe('ToggleSwitch', () => {
  it('flips data-on and notifies', () => {
    let checked = false
    const { getByRole } = render(<ToggleSwitch label="Crossfade" onChange={(v) => (checked = v)} />)
    const sw = getByRole('switch')
    expect(sw).toHaveAttribute('data-on', '0')
    fireEvent.click(sw)
    expect(checked).toBe(true)
  })
})

describe('SortableHeader', () => {
  it('marks the active column with an arrow', () => {
    render(
      <SortableHeader
        columns={[
          { key: 'Title', label: 'Song' },
          { key: 'Album', label: 'Album' },
        ]}
        sort="Title"
        dir="desc"
        onSort={() => {}}
        gridClass="grid-cols-2"
      />,
    )
    expect(screen.getByText('▼')).toBeInTheDocument()
  })
})

describe('SongRow', () => {
  it('highlights the active song with the orange ring', () => {
    const { container } = render(<SongRow song={song} active onPlay={() => {}} />)
    expect(container.firstChild).toHaveClass('ring-orange-500/40')
  })

  it('renders artist/album navigation when handlers are provided', () => {
    render(<SongRow song={song} active={false} onPlay={() => {}} onArtistClick={() => {}} onAlbumClick={() => {}} />)
    expect(screen.getByText('Artist').tagName).toBe('BUTTON')
    expect(screen.getByText('Album').tagName).toBe('BUTTON')
  })

  it('shows the formatted added date, or a dash without one', () => {
    const { rerender } = render(
      <SongRow song={{ ...song, dateDownload: '2026-10-04T12:00:00Z' }} active={false} onPlay={() => {}} />,
    )
    expect(screen.getByText(/2026/)).toBeInTheDocument()
    rerender(<SongRow song={{ ...song, dateDownload: null }} active={false} onPlay={() => {}} />)
    expect(screen.getByText('—')).toBeInTheDocument()
  })
})

describe('TagPills', () => {  const tags = [
    { id: 1, name: 'Tech', episodeCount: 2 },
    { id: 2, name: 'News', episodeCount: 0 },
  ]

  it('renders nothing without tags', () => {
    const { container } = render(<TagPills tags={[]} total={0} activeId={null} onSelect={() => {}} />)
    expect(container.firstChild).toBeNull()
  })

  it('selects and toggles off a tag', () => {
    let selected: number | null = null
    const onSelect = (id: number | null) => {
      selected = id
    }
    const { rerender } = render(<TagPills tags={tags} total={5} activeId={selected} onSelect={onSelect} />)
    expect(screen.getByText('All (5)')).toBeInTheDocument()
    expect(screen.getByText('Tech (2)')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Tech (2)'))
    expect(selected).toBe(1)
    rerender(<TagPills tags={tags} total={5} activeId={1} onSelect={onSelect} />)
    fireEvent.click(screen.getByText('Tech (2)'))
    expect(selected).toBeNull()
  })
})

describe('VisualizerBars', () => {
  it('renders four animated bars', () => {
    const { container } = render(<VisualizerBars />)
    expect(container.querySelectorAll('.visualizer-bar')).toHaveLength(4)
  })
})

describe('NowPlaying', () => {
  it('renders nothing without a current song', () => {
    const client = new QueryClient()
    const { container } = render(
      <QueryClientProvider client={client}>
        <NowPlaying onClose={() => {}} />
      </QueryClientProvider>,
    )
    expect(container.firstChild).toBeNull()
  })
})

describe('PlayerBar lyrics toggle', () => {
  function renderBar(onToggleLyrics = vi.fn()) {
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <PlayerBar
            onToggleQueue={() => {}}
            onToggleLyrics={onToggleLyrics}
            onOpenNowPlaying={() => {}}
          />
        </MemoryRouter>
      </QueryClientProvider>,
    )
    return onToggleLyrics
  }

  it('renders a lyrics toggle in both desktop and compact layouts', () => {
    renderBar()
    // Desktop row + compact row (hidden classes keep both in the DOM).
    expect(screen.getAllByTitle('Toggle Lyrics')).toHaveLength(2)
  })

  it('fires the toggle from the compact layout button', () => {
    const onToggleLyrics = renderBar()
    // Compact row renders first in the DOM; desktop row second.
    fireEvent.click(screen.getAllByTitle('Toggle Lyrics')[0])
    expect(onToggleLyrics).toHaveBeenCalledTimes(1)
  })
})
