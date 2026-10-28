import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DiscoverInsightsCard from './components/DiscoverInsightsCard'

const insights = {
  windowDays: 90,
  downloads: 1,
  dismissals: 1,
  previews: 2,
  clicks: 0,
  conversionRate: 0.5,
  completedPlays: 8,
  skippedPlays: 2,
  playbackCompletionRate: 0.8,
  uniqueArtists: 2,
  sources: [
    {
      source: 'graph',
      downloads: 0,
      dismissals: 1,
      previews: 0,
      clicks: 0,
      conversionRate: 0,
      learnedPrior: 0.25,
    },
    {
      source: 'similar',
      downloads: 1,
      dismissals: 0,
      previews: 2,
      clicks: 0,
      conversionRate: 1,
      learnedPrior: 0.75,
    },
  ],
}

function stub(body: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  )
}

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <DiscoverInsightsCard />
    </QueryClientProvider>,
  )
}

describe('DiscoverInsightsCard', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('renders conversion, playback, downloads and source chips', async () => {
    stub(insights)
    renderCard()

    expect(await screen.findByText('Your signal')).toBeInTheDocument()
    expect(screen.getByText('50%')).toBeInTheDocument()
    expect(screen.getByText('80%')).toBeInTheDocument()
    expect(screen.getByText('1 downloaded · 1 dismissed')).toBeInTheDocument()
    expect(screen.getByText(/Related · 0\/1/)).toBeInTheDocument()
    expect(screen.getByText(/Similar · 1\/1/)).toBeInTheDocument()
  })

  it('runs preset tuning and reports the applied model', async () => {
    const calls: string[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: unknown) => {
        const url = String(input)
        calls.push(url)
        const body = url.includes('/discover/tuning')
          ? {
              applied: true,
              insufficientData: false,
              preset: 'tag-heavy',
              snapshots: 12,
              labeled: 60,
              currentScore: 0.5,
              bestScore: 0.6,
              weights: {
                similarity: 0.25,
                tag: 0.45,
                affinity: 0.1,
                popularity: 0.1,
                source: 0.1,
                audio: 0.1,
              },
            }
          : insights
        return new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )
    renderCard()
    await screen.findByText('Your signal')

    fireEvent.click(screen.getByRole('button', { name: 'Tune weights' }))

    expect(await screen.findByText(/Weights updated \(tag-heavy\)/)).toBeInTheDocument()
    expect(calls.some((url) => url.includes('/discover/tuning'))).toBe(true)
  })

  it('stays hidden with no activity', async () => {
    stub({ ...insights, downloads: 0, dismissals: 0, previews: 0, clicks: 0, completedPlays: 0, skippedPlays: 0, sources: [] })
    renderCard()

    await waitFor(() => {
      expect(screen.queryByText('Your signal')).not.toBeInTheDocument()
    })
  })

  it('stays hidden when the insights payload is malformed', async () => {
    stub({ date: '2026-10-04', items: [], cached: false })
    renderCard()

    await waitFor(() => {
      expect(screen.queryByText('Your signal')).not.toBeInTheDocument()
    })
  })
})
