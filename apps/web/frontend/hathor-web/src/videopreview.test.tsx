import { render, screen, waitFor } from '@testing-library/react'
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
})
