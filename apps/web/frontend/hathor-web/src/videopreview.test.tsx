import { act, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import VideoPreview from './components/ui/VideoPreview'

const { mockPreview } = vi.hoisted(() => ({ mockPreview: vi.fn() }))

vi.mock('./api/client', () => ({
  api: { youtubePreview: (...args: unknown[]) => mockPreview(...args) },
}))

beforeEach(() => {
  mockPreview.mockReset()
})

describe('VideoPreview', () => {
  it('plays the iframe when embeddable', async () => {
    mockPreview.mockResolvedValue({ id: 'abc', embeddable: true, audioUrl: null })
    render(<VideoPreview id="abc" />)
    await waitFor(() => {
      const frame = document.querySelector('iframe')
      expect(frame).not.toBeNull()
      expect(frame?.getAttribute('src')).toContain('https://www.youtube.com/embed/abc')
    })
    expect(document.querySelector('audio')).toBeNull()
  })

  it('falls back to direct audio when embedding is blocked', async () => {
    mockPreview.mockResolvedValue({ id: 'abc', embeddable: false, audioUrl: 'https://r1---googlevideo.com/v' })
    render(<VideoPreview id="abc" title="Song" />)
    await waitFor(() => {
      const audio = document.querySelector('audio')
      expect(audio).not.toBeNull()
      expect(audio?.getAttribute('src')).toBe('https://r1---googlevideo.com/v')
    })
    expect(document.querySelector('iframe')).toBeNull()
    expect(screen.getByText('Watch on YouTube ↗')).toHaveAttribute(
      'href',
      'https://www.youtube.com/watch?v=abc',
    )
  })

  it('shows notice plus watch link when neither works', async () => {
    mockPreview.mockResolvedValue({ id: 'abc', embeddable: false, audioUrl: null })
    render(<VideoPreview id="abc" />)
    await waitFor(() => {
      expect(screen.getByText('No preview available for this video.')).toBeTruthy()
    })
    expect(document.querySelector('iframe')).toBeNull()
    expect(document.querySelector('audio')).toBeNull()
  })

  it('assumes embeddable when the probe itself fails', async () => {
    mockPreview.mockRejectedValue(new Error('offline'))
    render(<VideoPreview id="abc" />)
    await waitFor(() => {
      expect(document.querySelector('iframe')).not.toBeNull()
    })
  })

  describe('player runtime errors (oEmbed said yes, player says no)', () => {
    let onError: ((e: { data: number }) => void) | null
    let resolveCalls: string[]

    beforeEach(() => {
      onError = null
      resolveCalls = []
      Object.defineProperty(window, 'YT', {
        configurable: true,
        value: {
          // Regular function: the component constructs it with `new`.
          Player: vi.fn(function (
            _el: unknown,
            opts: { events?: { onError?: (e: { data: number }) => void } },
          ) {
            onError = opts.events?.onError ?? null
            return { destroy: vi.fn() }
          }),
        },
      })
      mockPreview.mockImplementation((id: unknown, resolveAudio?: unknown) => {
        if (resolveAudio) {
          resolveCalls.push(String(id))
          return Promise.resolve({ id, embeddable: true, audioUrl: 'https://r1---googlevideo.com/late' })
        }
        return Promise.resolve({ id, embeddable: true, audioUrl: null })
      })
    })

    it('error 153 swaps the iframe for lazily-resolved audio', async () => {
      render(<VideoPreview id="abc" />)
      await waitFor(() => {
        expect(document.querySelector('iframe')).not.toBeNull()
      })
      expect(onError).not.toBeNull()
      act(() => {
        onError?.({ data: 153 })
      })
      await waitFor(() => {
        expect(document.querySelector('audio')).not.toBeNull()
      })
      expect(resolveCalls).toEqual(['abc'])
      expect(document.querySelector('iframe')).toBeNull()
    })

    it('unresolvable audio ends at the watch link', async () => {
      mockPreview.mockImplementation((id: unknown, resolveAudio?: unknown) => {
        if (resolveAudio) return Promise.resolve({ id, embeddable: true, audioUrl: null })
        return Promise.resolve({ id, embeddable: true, audioUrl: null })
      })
      render(<VideoPreview id="abc" />)
      await waitFor(() => {
        expect(document.querySelector('iframe')).not.toBeNull()
      })
      act(() => {
        onError?.({ data: 101 })
      })
      await waitFor(() => {
        expect(screen.getByText('No preview available for this video.')).toBeTruthy()
      })
    })
  })
})
