import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import PlaylistFormModal from './components/ui/PlaylistFormModal'

const { mockUpdate } = vi.hoisted(() => ({ mockUpdate: vi.fn() }))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return { ...mod, api: { ...mod.api, updatePlaylist: mockUpdate } }
})

const playlist = {
  id: 7,
  title: 'Road',
  description: null,
  thumbnail: 'data:oldcover',
}

function renderModal() {
  const client = new QueryClient()
  return render(
    <QueryClientProvider client={client}>
      <PlaylistFormModal playlist={playlist} onClose={() => {}} onSaved={() => {}} />
    </QueryClientProvider>,
  )
}

describe('PlaylistFormModal cover remove', () => {
  it('shows Remove when editing a playlist with a cover', () => {
    renderModal()
    expect(screen.getByTitle('Remove Cover')).toBeInTheDocument()
  })

  it('sends REMOVE sentinel on save after remove', async () => {
    mockUpdate.mockResolvedValue({ ...playlist, thumbnail: null })
    renderModal()
    fireEvent.click(screen.getByTitle('Remove Cover'))
    fireEvent.click(screen.getByText('Save Changes'))
    await waitFor(() =>
      expect(mockUpdate).toHaveBeenCalledWith(7, expect.objectContaining({ thumbnail: 'REMOVE' })),
    )
  })

  it('hides Remove for new playlists', () => {
    const client = new QueryClient()
    render(
      <QueryClientProvider client={client}>
        <PlaylistFormModal onClose={() => {}} onSaved={() => {}} />
      </QueryClientProvider>,
    )
    expect(screen.queryByTitle('Remove Cover')).not.toBeInTheDocument()
  })
})
