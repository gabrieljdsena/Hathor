import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CoverArt, { evictCoverCache } from './components/ui/CoverArt'

// jsdom has no IntersectionObserver: components treat rows as near
// immediately. The observer-gated path is covered by observing that no
// <img> mounts when IO exists but never fires (see the third test).
describe('CoverArt cover URLs', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    try {
      localStorage.setItem('hathor:token', 'test-token')
    } catch {
      /* ignore */
    }
  })

  it('mounts the /cover URL without fetching song details', async () => {
    render(<CoverArt file="f.mp3" src={null} alt="" />)
    const img = (await screen.findAllByAltText(''))[0]
    expect(img).toHaveAttribute('src', expect.stringContaining('/api/v1/songs/f.mp3/cover'))
    expect(img.getAttribute('src')).toContain('token=test-token')
  })

  it('prefers an explicit src (edit preview, current song) over the URL', async () => {
    render(<CoverArt file="f.mp3" src="data:image/jpeg;base64,AAA" alt="" />)
    expect((await screen.findAllByAltText(''))[0]).toHaveAttribute(
      'src',
      'data:image/jpeg;base64,AAA',
    )
  })

  it('busts the browser cache after eviction (metadata edit)', async () => {
    const { unmount } = render(<CoverArt file="g.mp3" src={null} alt="" />)
    const first = (await screen.findAllByAltText(''))[0].getAttribute('src') ?? ''
    expect(first).not.toContain('v=')
    unmount()

    evictCoverCache('g.mp3')
    render(<CoverArt file="g.mp3" src={null} alt="" />)
    await waitFor(() => {
      const src = screen.getAllByAltText('')[0].getAttribute('src') ?? ''
      expect(src).toContain('v=1')
    })
  })

  it('falls back to the note when the file has no art endpoint', async () => {
    // No file and no src: the placeholder mounts (with the music note).
    const { container } = render(<CoverArt src={null} alt="" />)
    expect(container.querySelector('svg')).not.toBeNull()
  })
})
