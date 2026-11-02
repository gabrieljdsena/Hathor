import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Download from './views/Download'

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

function stubFetch(
  handler: (input: unknown, init?: RequestInit) => unknown | Promise<unknown>,
) {
  const calls: { input: unknown; init?: RequestInit }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown, init?: RequestInit) => {
      calls.push({ input, init })
      const body = await handler(input, init)
      return jsonResponse(body)
    }),
  )
  return calls
}

function renderDownload() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <Download />
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('Download search', () => {
  it('shows an explicit empty result instead of a blank page', async () => {
    const calls = stubFetch((input) => {
      const url = String(input)
      if (url.includes('/youtube/search')) return []
      return []
    })
    renderDownload()

    fireEvent.change(
      screen.getByPlaceholderText('Search for songs, artists, or albums...'),
      { target: { value: 'zzzz' } },
    )
    fireEvent.click(screen.getByRole('button', { name: 'Search Songs' }))

    expect(
      (await screen.findAllByText(/No results for “zzzz”/)).length,
    ).toBeGreaterThan(0)
    expect(
      calls.some((call) => String(call.input).includes('/youtube/search?q=zzzz')),
    ).toBe(true)
  })

  it('explains when Search is clicked with an empty query', async () => {
    const calls = stubFetch(() => [])
    renderDownload()

    fireEvent.click(screen.getByRole('button', { name: 'Search Songs' }))

    expect(
      await screen.findByText('Enter a song, artist, or album to search.'),
    ).toBeInTheDocument()
    expect(
      calls.some((call) => String(call.input).includes('/youtube/search')),
    ).toBe(false)
  })

  it('submits only one job for a same-tick double click', async () => {
    const calls = stubFetch((input) => {
      const url = String(input)
      if (url.includes('/youtube/search')) {
        return [
          {
            id: 'vid123',
            title: 'Midnight City',
            uploader: 'M83',
            durationSec: 240,
            thumbnail: '',
          },
        ]
      }
      if (url.includes('/downloads/check')) return { owned: false, file: null }
      if (url.endsWith('/downloads')) return { qid: 'q1' }
      return []
    })
    renderDownload()

    fireEvent.change(
      screen.getByPlaceholderText('Search for songs, artists, or albums...'),
      { target: { value: 'Midnight City' } },
    )
    fireEvent.click(screen.getByRole('button', { name: 'Search Songs' }))
    const download = await screen.findByRole('button', {
      name: 'Download Midnight City',
    })

    fireEvent.click(download)
    fireEvent.click(download)

    await screen.findByText('Download queued: Midnight City')
    expect(
      calls.filter(
        (call) =>
          String(call.input).endsWith('/downloads') &&
          call.init?.method === 'POST',
      ),
    ).toHaveLength(1)
  })

  it('re-downloads an owned song onto the existing file', async () => {
    const calls = stubFetch((input) => {
      const url = String(input)
      if (url.includes('/youtube/search')) {
        return [
          {
            id: 'vid123',
            title: 'Midnight City',
            uploader: 'M83',
            durationSec: 240,
            thumbnail: '',
          },
        ]
      }
      if (url.includes('/downloads/check')) {
        return { owned: true, file: 'Midnight City.mp3' }
      }
      if (url.endsWith('/downloads')) return { qid: 'q1' }
      return []
    })
    renderDownload()

    fireEvent.change(
      screen.getByPlaceholderText('Search for songs, artists, or albums...'),
      { target: { value: 'Midnight City' } },
    )
    fireEvent.click(screen.getByRole('button', { name: 'Search Songs' }))
    fireEvent.click(
      await screen.findByRole('button', { name: 'Download Midnight City' }),
    )

    expect(await screen.findByText('Already in your library')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Replace file'))

    await screen.findByText('Download queued: Midnight City')
    const post = calls.find(
      (call) =>
        String(call.input).endsWith('/downloads') &&
        call.init?.method === 'POST',
    )
    expect(String(post?.init?.body)).toContain(
      '"TargetFile":"Midnight City.mp3"',
    )
  })

  it('keeps the latest search when an older request resolves later', async () => {
    let resolveAlpha!: (response: Response) => void
    let resolveBeta!: (response: Response) => void
    const alpha = new Promise<Response>((resolve) => {
      resolveAlpha = resolve
    })
    const beta = new Promise<Response>((resolve) => {
      resolveBeta = resolve
    })

    vi.stubGlobal(
      'fetch',
      vi.fn((input: unknown) => {
        const url = String(input)
        if (url.includes('/metadata/trending')) return jsonResponse(['Beta'])
        if (url.includes('/youtube/search')) {
          if (url.includes('q=Alpha')) return alpha
          if (url.includes('q=Beta')) return beta
        }
        return jsonResponse([])
      }),
    )

    renderDownload()
    fireEvent.change(
      screen.getByPlaceholderText('Search for songs, artists, or albums...'),
      { target: { value: 'Alpha' } },
    )
    fireEvent.click(screen.getByRole('button', { name: 'Search Songs' }))
    await screen.findByText('Searching YouTube…')

    fireEvent.click(await screen.findByRole('button', { name: 'Beta' }))
    await act(async () => {
      resolveBeta(
        jsonResponse([
          {
            id: 'beta',
            title: 'Beta Hit',
            uploader: 'Beta Artist',
            durationSec: 200,
            thumbnail: '',
          },
        ]),
      )
    })
    expect(await screen.findByText('Beta Hit')).toBeInTheDocument()

    await act(async () => {
      resolveAlpha(
        jsonResponse([
          {
            id: 'alpha',
            title: 'Alpha Hit',
            uploader: 'Alpha Artist',
            durationSec: 200,
            thumbnail: '',
          },
        ]),
      )
      await Promise.resolve()
    })

    expect(screen.queryByText('Alpha Hit')).not.toBeInTheDocument()
    expect(screen.getByText('1 result for “Beta”')).toBeInTheDocument()
  })
})
