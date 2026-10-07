import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import Discover from './views/Discover'
import type { Discover as DiscoverPayload } from './api/client'

const payload: DiscoverPayload = {
  date: '2026-10-04',
  cached: false,
  items: [
    {
      title: 'Midnight City',
      artist: 'M83',
      album: 'Hurry Up',
      year: '2011',
      genre: 'Electronic',
      artworkUrl: 'http://art/m83.jpg',
      source: 'artist',
      score: 0.95,
    },
    {
      title: 'Chart Hit',
      artist: 'Somebody',
      album: '',
      year: '',
      genre: 'Pop',
      artworkUrl: '',
      source: 'chart',
      score: 0.4,
    },
  ],
}

function stubFetch(handler: (input: unknown, init?: RequestInit) => unknown) {
  const calls: { input: unknown; init?: RequestInit }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      calls.push({ input, init })
      const body = await handler(input, init)
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )
  return calls
}

function renderDiscover() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <Discover />
    </QueryClientProvider>,
  )
}

describe('Discover view', () => {
  it('renders suggestion cards with source badges', async () => {
    stubFetch(() => payload)
    renderDiscover()

    expect(await screen.findByText('Midnight City')).toBeInTheDocument()
    expect(screen.getAllByText('From your artists')).toHaveLength(2) // chip + card badge
    expect(screen.getByRole('button', { name: 'Trending' })).toBeInTheDocument()
  })

  it('filters by source chip', async () => {
    stubFetch(() => payload)
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByRole('button', { name: 'Trending' }))
    expect(screen.queryByText('Midnight City')).not.toBeInTheDocument()
    expect(screen.getByText('Chart Hit')).toBeInTheDocument()
  })

  it('queues a download and shows a notice', async () => {
    const calls = stubFetch((input) =>
      String(input).endsWith('/downloads') ? { qid: 'abc' } : payload,
    )
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByLabelText('Download Midnight City by M83'))
    expect(await screen.findByText('Download queued: Midnight City')).toBeInTheDocument()
    const post = calls.find((c) => String(c.input).endsWith('/downloads'))
    expect(post?.init?.method).toBe('POST')
    expect(String(post?.init?.body)).toContain('M83')
  })

  it('ignores a same-tick double click on download', async () => {
    const calls = stubFetch((input) =>
      String(input).endsWith('/downloads') ? { qid: 'abc' } : payload,
    )
    renderDiscover()
    await screen.findByText('Midnight City')

    const button = screen.getByLabelText('Download Midnight City by M83')
    fireEvent.click(button)
    fireEvent.click(button)
    await screen.findByText('Download queued: Midnight City')
    expect(calls.filter((c) => String(c.input).endsWith('/downloads'))).toHaveLength(1)
  })

  it('refreshes suggestions on demand', async () => {
    const calls = stubFetch((input) =>
      String(input).endsWith('/discover/refresh')
        ? { ...payload, cached: false }
        : payload,
    )
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByText('Refresh'))
    expect(screen.getByText('Refreshing suggestions…')).toBeInTheDocument()
    expect(screen.getByText('Refreshing…')).toBeDisabled()
    expect(calls.some((c) => String(c.input).endsWith('/discover/refresh'))).toBe(true)
  })

  it('opens a YouTube preview for a suggestion', async () => {
    stubFetch((input) => {
      const url = String(input)
      if (url.includes('/youtube/search')) {
        return [{ id: 'vid123', title: 'Midnight City', uploader: 'M83', durationSec: 240, thumbnail: '' }]
      }
      if (url.includes('/youtube/preview')) {
        return { id: 'vid123', embeddable: true, audioUrl: null }
      }
      return payload
    })
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByLabelText('Preview Midnight City by M83'))
    let frame: HTMLIFrameElement | null = null
    await waitFor(() => {
      frame = document.querySelector('iframe')
      expect(frame).not.toBeNull()
    })
    expect(frame!.getAttribute('src')).toContain('https://www.youtube.com/embed/vid123?autoplay=1')
    expect(frame!.getAttribute('src')).toContain('enablejsapi=1')
  })

  it('shows a notice when no preview exists', async () => {
    stubFetch((input) => (String(input).includes('/youtube/search') ? [] : payload))
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByLabelText('Preview Midnight City by M83'))
    expect(await screen.findByText('No YouTube preview found for Midnight City.')).toBeInTheDocument()
  })

  it('asks before downloading an owned song, submits on confirm', async () => {
    const calls = stubFetch((input) => {
      if (String(input).includes('/downloads/check')) return { owned: true, file: 'Midnight City.mp3' }
      if (String(input).endsWith('/downloads')) return { qid: 'abc' }
      return payload
    })
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByLabelText('Download Midnight City by M83'))
    expect(await screen.findByText('Already in your library')).toBeInTheDocument()
    expect(calls.some((c) => String(c.input).endsWith('/downloads'))).toBe(false)

    fireEvent.click(screen.getByText('Download anyway'))
    expect(await screen.findByText('Download queued: Midnight City')).toBeInTheDocument()
    expect(calls.some((c) => String(c.input).endsWith('/downloads'))).toBe(true)
  })

  it('cancelling the owned-song confirm submits nothing', async () => {
    const calls = stubFetch((input) => {
      if (String(input).includes('/downloads/check')) return { owned: true, file: 'Midnight City.mp3' }
      if (String(input).endsWith('/downloads')) return { qid: 'abc' }
      return payload
    })
    renderDiscover()
    await screen.findByText('Midnight City')

    fireEvent.click(screen.getByLabelText('Download Midnight City by M83'))
    expect(await screen.findByText('Already in your library')).toBeInTheDocument()

    fireEvent.click(screen.getByText('Cancel'))
    expect(calls.some((c) => String(c.input).endsWith('/downloads'))).toBe(false)
    expect(screen.queryByText('Already in your library')).not.toBeInTheDocument()
  })
})
