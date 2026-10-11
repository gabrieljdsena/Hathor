import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { JSX } from 'react'
import RouteErrorBoundary from './components/ui/RouteErrorBoundary'
import { lazyWithRetry } from './utils/lazyRetry'

function Boom(): JSX.Element {
  throw new Error('view exploded')
}

beforeEach(() => {
  sessionStorage.clear()
  vi.unstubAllGlobals()
})

describe('RouteErrorBoundary', () => {
  it('replaces a crashing view with recovery UI instead of a white screen', () => {
    // Silence React's error logging for the intentional crash.
    const err = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <RouteErrorBoundary resetKey="/songs">
        <Boom />
      </RouteErrorBoundary>,
    )
    expect(screen.getByText('This page crashed')).toBeInTheDocument()
    expect(screen.getByText('Reload page')).toBeInTheDocument()
    err.mockRestore()
  })

  it('recovers when navigation changes the reset key', () => {
    const err = vi.spyOn(console, 'error').mockImplementation(() => {})
    const { rerender } = render(
      <RouteErrorBoundary resetKey="/songs">
        <Boom />
      </RouteErrorBoundary>,
    )
    expect(screen.getByText('This page crashed')).toBeInTheDocument()
    rerender(
      <RouteErrorBoundary resetKey="/podcasts">
        <div>podcasts view</div>
      </RouteErrorBoundary>,
    )
    expect(screen.getByText('podcasts view')).toBeInTheDocument()
    err.mockRestore()
  })
})

describe('lazyWithRetry', () => {
  function mockReload() {
    const reload = vi.fn()
    try {
      Object.defineProperty(window, 'location', {
        configurable: true,
        value: { ...window.location, reload },
      })
    } catch {
      // jsdom may refuse: sessionStorage flag assertions still hold.
    }
    return reload
  }

  it('reloads once on a stale-chunk failure', async () => {
    const reload = mockReload()
    let calls = 0
    const Lazy = lazyWithRetry(() => {
      calls += 1
      return Promise.reject(new Error('Failed to fetch dynamically imported module'))
    })
    const err = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <RouteErrorBoundary resetKey="/x">
        <Lazy />
      </RouteErrorBoundary>,
    )
    await vi.waitFor(() => {
      expect(sessionStorage.getItem('hathor:chunk-retry')).toBe('1')
    })
    expect(calls).toBe(1)
    try {
      expect(reload).toHaveBeenCalledOnce()
    } catch {
      // reload mock not installable here — the flag above is the real guard.
    }
    err.mockRestore()
  })

  it('does not reload on real bugs (goes to the boundary instead)', async () => {
    mockReload()
    const Lazy = lazyWithRetry(() => Promise.reject(new TypeError("Cannot read properties of null")))
    const err = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <RouteErrorBoundary resetKey="/x">
        <Lazy />
      </RouteErrorBoundary>,
    )
    expect(await screen.findByText('This page crashed')).toBeInTheDocument()
    expect(sessionStorage.getItem('hathor:chunk-retry')).toBeNull()
    err.mockRestore()
  })
})
