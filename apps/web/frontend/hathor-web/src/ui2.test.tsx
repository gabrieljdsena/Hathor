import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import type { ReactNode } from 'react'
import EditSongModal from './components/ui/EditSongModal'
import Modal from './components/ui/Modal'
import { ControlButton, PlayCircleButton } from './components/ui/buttons'
import { GhostButton, PrimaryButton } from './components/ui/fields'
import type { Song } from './api/client'

const song: Song = {
  file: 's.mp3',
  artist: 'Artist',
  title: 'Title',
  album: 'Album',
  year: '2020',
  duration: 180,
  coverArt: 'data:image/jpeg;base64,AAA',
  dateDownload: null,
}

function Providers({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>
}

describe('Modal', () => {
  it('renders nothing when closed and closes on Escape', () => {
    const onClose = vi.fn()
    const { rerender } = render(
      <Modal open={false} onClose={onClose} title="T">
        body
      </Modal>,
    )
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    rerender(
      <Modal open onClose={onClose} title="T">
        body
      </Modal>,
    )
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    fireEvent.keyDown(window, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(1)
  })
})

describe('EditSongModal REMOVE flow', () => {
  it('sends the REMOVE sentinel when cover is removed', async () => {
    const seen: RequestInit[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if ((init?.method ?? 'GET') !== 'GET') seen.push(init ?? {})
        if (init?.method === 'PATCH') {
          return Response.json({ song, resumeSec: 0 })
        }
        return Response.json(song)
      }),
    )
    const onSaved = vi.fn()
    render(
      <Providers>
        <EditSongModal song={song} onClose={() => {}} onSaved={onSaved} />
      </Providers>,
    )
    fireEvent.click(screen.getByTitle('Remove Cover'))
    fireEvent.click(screen.getByText('Save Changes'))
    await waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1))
    const patch = seen.find((i) => i.method === 'PATCH')
    expect(patch).toBeDefined()
    expect(patch!.body as string).toContain('"CoverArt":"REMOVE"')
    vi.unstubAllGlobals()
  })
})

describe('Button loading states', () => {
  it('PrimaryButton shows a spinner and disables while loading', () => {
    const { container, rerender } = render(<PrimaryButton loading={false}>Save</PrimaryButton>)
    const btn = screen.getByRole('button', { name: 'Save' })
    expect(btn).not.toBeDisabled()
    expect(container.querySelector('.spinner-btn')).toBeNull()
    rerender(<PrimaryButton loading>Save</PrimaryButton>)
    expect(screen.getByRole('button')).toBeDisabled()
    expect(screen.getByRole('button')).toHaveAttribute('aria-busy', 'true')
    expect(container.querySelector('.spinner-btn')).not.toBeNull()
  })

  it('GhostButton shows a spinner and disables while loading', () => {
    render(<GhostButton loading>Cancel</GhostButton>)
    expect(screen.getByRole('button')).toBeDisabled()
    expect(document.querySelector('.spinner-btn')).not.toBeNull()
  })

  it('PlayCircleButton swaps its icon for a spinner while loading', () => {
    const { container } = render(<PlayCircleButton loading />)
    expect(container.querySelector('.spinner-btn')).not.toBeNull()
    expect(container.querySelector('svg')).toBeNull()
  })

  it('ControlButton swaps its icon for a spinner while loading', () => {
    const { container } = render(<ControlButton icon="play" title="Play" loading />)
    expect(screen.getByRole('button')).toBeDisabled()
    expect(container.querySelector('.spinner-btn')).not.toBeNull()
  })
})
