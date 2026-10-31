import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type DiscoverItem } from './api/client'
import { useDiscoverPreviewQueue } from './hooks/useDiscoverPreviewQueue'
import { usePlayer } from './store/player'

const originalToggle = usePlayer.getState().toggle

function item(title: string, artist = 'Artist'): DiscoverItem {
  return {
    title,
    artist,
    album: '',
    year: '',
    genre: '',
    artworkUrl: '',
    source: 'similar',
    score: 0.5,
  }
}

function stubYoutube(audioFor: (id: string) => string | null = (id) => `https://audio/${id}`) {
  vi.spyOn(api, 'youtubeSearch').mockImplementation(async (query) => [
    {
      id: query,
      title: query,
      uploader: '',
      durationSec: 180,
      thumbnail: '',
    },
  ])
  vi.spyOn(api, 'youtubePreview').mockImplementation(async (id) => ({
    id,
    embeddable: true,
    audioUrl: audioFor(id),
  }))
}

beforeEach(() => {
  usePlayer.setState({ isPlaying: false, toggle: originalToggle })
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
})

afterEach(() => {
  vi.restoreAllMocks()
  usePlayer.setState({ isPlaying: false, toggle: originalToggle })
})

describe('useDiscoverPreviewQueue', () => {
  it('streams the first visible item and caps the queue at 20', async () => {
    stubYoutube()
    const items = Array.from({ length: 25 }, (_, i) => item(`Track ${i}`))
    const onStarted = vi.fn()
    const { result } = renderHook(() => useDiscoverPreviewQueue(onStarted))

    await act(async () => {
      await result.current.start(items)
    })

    expect(result.current.tracks).toHaveLength(20)
    await waitFor(() => {
      expect(result.current.current?.status).toBe('playing')
    })
    expect(result.current.current?.item.title).toBe('Track 0')
    expect(result.current.audioRef.current?.getAttribute('src')).toContain(
      'https://audio/',
    )
    expect(onStarted).toHaveBeenCalledWith(items[0])
  })

  it('starts at the clicked card and queues the following visible items', async () => {
    stubYoutube()
    const items = [item('One'), item('Two'), item('Three')]
    const { result } = renderHook(() => useDiscoverPreviewQueue())

    await act(async () => {
      await result.current.start(items, 1)
    })

    expect(result.current.tracks.map((track) => track.item.title)).toEqual([
      'Two',
      'Three',
    ])
    await waitFor(() => {
      expect(result.current.current?.item.title).toBe('Two')
    })
  })

  it('advances when the stream ends', async () => {
    stubYoutube()
    const items = [item('One'), item('Two')]
    const { result } = renderHook(() => useDiscoverPreviewQueue())

    await act(async () => {
      await result.current.start(items)
    })
    await waitFor(() => {
      expect(result.current.current?.item.title).toBe('One')
    })

    act(() => {
      result.current.audioRef.current?.dispatchEvent(new Event('ended'))
    })

    await waitFor(() => {
      expect(result.current.current?.item.title).toBe('Two')
      expect(result.current.current?.status).toBe('playing')
    })
  })

  it('skips unavailable tracks and reports why', async () => {
    vi.spyOn(api, 'youtubeSearch').mockImplementation(async (query) => [
      {
        id: query,
        title: query,
        uploader: '',
        durationSec: 180,
        thumbnail: '',
      },
    ])
    vi.spyOn(api, 'youtubePreview').mockImplementation(async (id) => ({
      id,
      embeddable: true,
      audioUrl: id.startsWith('Broken') ? null : 'https://audio/good',
    }))
    const items = [item('Broken'), item('Good')]
    const { result } = renderHook(() => useDiscoverPreviewQueue())

    await act(async () => {
      await result.current.start(items)
    })

    await waitFor(() => {
      expect(result.current.current?.item.title).toBe('Good')
      expect(result.current.current?.status).toBe('playing')
    })
    expect(result.current.tracks[0].status).toBe('failed')
    expect(result.current.notice).toContain('Preview unavailable: Broken')
  })

  it('keeps a resolved track ready when autoplay is blocked', async () => {
    stubYoutube()
    const play = vi.mocked(HTMLMediaElement.prototype.play)
    play
      .mockRejectedValueOnce(new DOMException('blocked', 'NotAllowedError'))
      .mockResolvedValue(undefined)
    const onStarted = vi.fn()
    const { result } = renderHook(() => useDiscoverPreviewQueue(onStarted))

    await act(async () => {
      await result.current.start([item('One')])
    })

    await waitFor(() => {
      expect(result.current.current?.status).toBe('ready')
    })
    expect(result.current.playing).toBe(false)
    expect(result.current.notice).toContain('Press play')
    expect(onStarted).not.toHaveBeenCalled()

    act(() => {
      result.current.toggle()
    })

    await waitFor(() => {
      expect(result.current.current?.status).toBe('playing')
      expect(onStarted).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'One' }),
      )
    })
  })

  it('pauses the main player before starting a preview', async () => {
    stubYoutube()
    const toggle = vi.fn(async () => {
      usePlayer.setState({ isPlaying: false })
    })
    usePlayer.setState({ isPlaying: true, toggle })
    const { result } = renderHook(() => useDiscoverPreviewQueue())

    await act(async () => {
      await result.current.start([item('One')])
    })

    expect(toggle).toHaveBeenCalledTimes(1)
    await waitFor(() => {
      expect(result.current.current?.status).toBe('playing')
    })
  })

  it('closes the queue and stops the stream', async () => {
    stubYoutube()
    const { result } = renderHook(() => useDiscoverPreviewQueue())

    await act(async () => {
      await result.current.start([item('One')])
    })
    expect(result.current.active).toBe(true)

    act(() => {
      result.current.close()
    })

    expect(result.current.tracks).toHaveLength(0)
    expect(result.current.active).toBe(false)
    expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled()
  })
})
