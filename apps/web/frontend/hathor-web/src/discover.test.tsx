import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
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
})
