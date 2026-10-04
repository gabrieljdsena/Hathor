import { act, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import Pagination from './components/ui/Pagination'
import QueueSheet from './components/QueueSheet'
import { usePlayer } from './store/player'
import type { Song } from './api/client'

function song(file: string, n: number): Song {
  return {
    file,
    artist: 'Artist',
    title: `Song ${n}`,
    album: 'Album',
    year: '2020',
    duration: 180,
    coverArt: null,
    dateDownload: null,
  }
}

describe('Pagination', () => {
  it('renders nothing for a single page', () => {
    const { container } = render(<Pagination page={1} totalPages={1} onChange={() => {}} />)
    expect(container.firstChild).toBeNull()
  })

  it('disables prev on first page and next on last', () => {
    const { rerender } = render(<Pagination page={1} totalPages={3} onChange={() => {}} />)
    expect(screen.getByLabelText('Previous page')).toBeDisabled()
    expect(screen.getByLabelText('Next page')).not.toBeDisabled()
    rerender(<Pagination page={3} totalPages={3} onChange={() => {}} />)
    expect(screen.getByLabelText('Next page')).toBeDisabled()
  })

  it('collapses far pages with ellipsis and navigates', () => {
    const onChange = vi.fn()
    render(<Pagination page={5} totalPages={10} onChange={onChange} />)
    for (const n of [1, 4, 5, 6, 10]) {
      expect(screen.getByLabelText(`Page ${n}`)).toBeInTheDocument()
    }
    expect(screen.queryByLabelText('Page 2')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Page 8')).not.toBeInTheDocument()
    fireEvent.click(screen.getByLabelText('Page 10'))
    expect(onChange).toHaveBeenCalledWith(10)
    fireEvent.click(screen.getByLabelText('Next page'))
    expect(onChange).toHaveBeenCalledWith(6)
  })
})

describe('QueueSheet paging', () => {
  function renderSheet(queue: Song[]) {
    usePlayer.setState({ queue, currentSong: queue[0] ?? null })
    const client = new QueryClient()
    return render(
      <QueryClientProvider client={client}>
        <QueueSheet open onClose={() => {}} />
      </QueryClientProvider>,
    )
  }

  it('renders one page of rows with a pager for long queues', () => {
    const queue = Array.from({ length: 120 }, (_, i) => song(`s${i}.mp3`, i))
    const { container } = renderSheet(queue)
    expect(container.querySelectorAll('#queue-body > div')).toHaveLength(50)
    expect(screen.getByLabelText('Page 3')).toBeInTheDocument()
  })

  it('navigates pages and follows the current song', () => {
    const queue = Array.from({ length: 120 }, (_, i) => song(`s${i}.mp3`, i))
    const { container } = renderSheet(queue)
    fireEvent.click(screen.getByLabelText('Page 3'))
    const rows = [...container.querySelectorAll('#queue-body > div')];
    expect(rows).toHaveLength(20)
    expect(rows[0].textContent).toContain('Song 100')

    // Jumping the current song to another page moves the pager with it.
    act(() => {
      usePlayer.setState({ currentSong: queue[10] })
    })
    const first = [...container.querySelectorAll('#queue-body > div')][0]
    expect(first.textContent).toContain('Song 0')
  })
})
