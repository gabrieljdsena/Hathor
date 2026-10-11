import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CoverArt, { evictCoverCache } from './components/ui/CoverArt'

const { mockSong } = vi.hoisted(() => ({ mockSong: vi.fn() }))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return { ...mod, api: { ...mod.api, song: mockSong } }
})

describe('CoverArt cache', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('serves the cached cover without refetching', async () => {
    mockSong.mockResolvedValue({ coverArt: 'data:AAA' })
    const { unmount } = render(<CoverArt file="f.mp3" src={null} alt="" />)
    expect(await screen.findByAltText('')).toHaveAttribute('src', 'data:AAA')
    expect(mockSong).toHaveBeenCalledTimes(1)
    unmount()

    // Second mount reuses the module cache — no second fetch.
    render(<CoverArt file="f.mp3" src={null} alt="" />)
    expect(await screen.findAllByAltText('')).toHaveLength(1)
    expect(mockSong).toHaveBeenCalledTimes(1)
  })

  it('caps concurrent detail fetches so covers cannot starve transport', async () => {
    // Six rows mount at once (fresh list render): only four detail fetches
    // may be inflight — the rest wait instead of saturating the browser's
    // connection pool and stalling play/toggle behind covers.
    const resolvers: Array<(v: unknown) => void> = []
    mockSong.mockImplementation(() => new Promise((r) => resolvers.push(r)))
    const mounts = Array.from({ length: 6 }, (_, i) =>
      render(<CoverArt file={`cap-${i}.mp3`} src={null} alt="" />),
    )
    await waitFor(() => expect(mockSong).toHaveBeenCalledTimes(4))
    expect(resolvers).toHaveLength(4)
    resolvers.splice(0).forEach((r, i) => r({ coverArt: `data:CAP${i}` }))
    await waitFor(() => expect(mockSong).toHaveBeenCalledTimes(6))
    resolvers.splice(0).forEach((r, i) => r({ coverArt: `data:CAP${i + 4}` }))
    await waitFor(() => expect(screen.getAllByAltText('')).toHaveLength(6))
    mounts.forEach((m) => m.unmount())
  })

  it('refetches after eviction (metadata edit / remove / delete)', async () => {
    mockSong.mockResolvedValueOnce({ coverArt: 'data:OLD' })
    const { unmount } = render(<CoverArt file="g.mp3" src={null} alt="" />)
    expect(await screen.findByAltText('')).toHaveAttribute('src', 'data:OLD')
    unmount()

    evictCoverCache('g.mp3')
    mockSong.mockResolvedValueOnce({ coverArt: 'data:NEW' })
    render(<CoverArt file="g.mp3" src={null} alt="" />)
    await waitFor(() =>
      expect(screen.getAllByAltText('')[0]).toHaveAttribute('src', 'data:NEW'),
    )
    expect(mockSong).toHaveBeenCalledTimes(2)
  })
})
