import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createUnloadPause } from './unloadPause'

function firePageHide() {
  window.dispatchEvent(new Event('pagehide'))
}

describe('createUnloadPause', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('sends an authenticated keepalive pause when playing', () => {
    localStorage.setItem('hathor:token', 'tok123')
    const post = vi.fn()
    const stop = createUnloadPause(
      () => ({ isPlaying: true, hasTrack: true }),
      () => localStorage.getItem('hathor:token'),
      post,
    )
    firePageHide()
    expect(post).toHaveBeenCalledOnce()
    expect(post).toHaveBeenCalledWith('/api/v1/player/pause', {
      method: 'POST',
      headers: { Authorization: 'Bearer tok123' },
      keepalive: true,
    })
    stop()
  })

  it('stays silent when paused, trackless, or logged out', () => {
    const post = vi.fn()
    const stop1 = createUnloadPause(() => ({ isPlaying: false, hasTrack: true }), () => 'tok', post)
    const stop2 = createUnloadPause(() => ({ isPlaying: true, hasTrack: false }), () => 'tok', post)
    const stop3 = createUnloadPause(() => ({ isPlaying: true, hasTrack: true }), () => null, post)
    firePageHide()
    expect(post).not.toHaveBeenCalled()
    stop1()
    stop2()
    stop3()
  })

  it('cleanup removes the listener', () => {
    localStorage.setItem('hathor:token', 'tok123')
    const post = vi.fn()
    const stop = createUnloadPause(() => ({ isPlaying: true, hasTrack: true }), () => 'tok123', post)
    stop()
    firePageHide()
    expect(post).not.toHaveBeenCalled()
  })

  it('survives throwing readers', () => {
    const post = vi.fn()
    const stop = createUnloadPause(
      () => {
        throw new Error('store gone')
      },
      () => {
        throw new Error('storage gone')
      },
      post,
    )
    expect(() => firePageHide()).not.toThrow()
    expect(post).not.toHaveBeenCalled()
    stop()
  })
})
