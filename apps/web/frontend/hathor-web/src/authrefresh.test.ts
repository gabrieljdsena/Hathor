import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api/client'

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function mockFetch(handler: (url: string) => Response | Promise<Response>) {
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      calls.push(url)
      return handler(url)
    }),
  )
  return calls
}

describe('silent auth refresh', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.unstubAllGlobals()
    delete (navigator as unknown as { locks?: unknown }).locks
    window.history.replaceState(null, '', '/')
  })

  it('refreshes and retries an authenticated auth endpoint', async () => {
    localStorage.setItem('hathor:token', 'stale')
    localStorage.setItem('hathor:refresh', 'refresh-1')
    let meCalls = 0
    const calls = mockFetch((url) => {
      if (url === '/api/v1/auth/me') {
        meCalls += 1
        return meCalls === 1
          ? json({ message: 'Unauthorized' }, 401)
          : json({ userId: 'u', username: 'gabriel' })
      }
      if (url === '/api/v1/auth/refresh') {
        return json({ accessToken: 'fresh', refreshToken: 'refresh-2' })
      }
      throw new Error(`unexpected ${url}`)
    })

    await expect(api.me()).resolves.toEqual({ userId: 'u', username: 'gabriel' })
    expect(calls.filter((url) => url.endsWith('/auth/refresh'))).toHaveLength(1)
    expect(localStorage.getItem('hathor:token')).toBe('fresh')
    expect(localStorage.getItem('hathor:refresh')).toBe('refresh-2')
  })

  it('keeps the session when refresh is temporarily unavailable', async () => {
    localStorage.setItem('hathor:token', 'stale')
    localStorage.setItem('hathor:refresh', 'refresh-1')
    mockFetch((url) => {
      if (url === '/api/v1/auth/me') return json({ message: 'Unauthorized' }, 401)
      if (url === '/api/v1/auth/refresh') throw new TypeError('offline')
      throw new Error(`unexpected ${url}`)
    })

    await expect(api.me()).rejects.toThrow(/refresh unavailable/i)
    expect(localStorage.getItem('hathor:token')).toBe('stale')
    expect(localStorage.getItem('hathor:refresh')).toBe('refresh-1')
  })

  it('clears the session only when refresh is explicitly rejected', async () => {
    window.history.replaceState(null, '', '/login')
    localStorage.setItem('hathor:token', 'stale')
    localStorage.setItem('hathor:refresh', 'refresh-1')
    localStorage.setItem('hathor:username', 'gabriel')
    mockFetch((url) => {
      if (url === '/api/v1/auth/me') return json({ message: 'Unauthorized' }, 401)
      if (url === '/api/v1/auth/refresh') return json({ message: 'Invalid' }, 401)
      throw new Error(`unexpected ${url}`)
    })

    await expect(api.me()).rejects.toThrow('Unauthorized')
    expect(localStorage.getItem('hathor:token')).toBeNull()
    expect(localStorage.getItem('hathor:refresh')).toBeNull()
    expect(localStorage.getItem('hathor:username')).toBeNull()
  })

  it('never refreshes for anonymous auth endpoints (bad login)', async () => {
    localStorage.setItem('hathor:token', 'keep')
    localStorage.setItem('hathor:refresh', 'keep-refresh')
    const calls = mockFetch((url) => {
      if (url === '/api/v1/auth/login') return json({ message: 'Invalid credentials' }, 401)
      throw new Error(`unexpected ${url}`)
    })

    await expect(api.login('gabriel', 'wrong')).rejects.toThrow()
    expect(calls.some((url) => url.endsWith('/auth/refresh'))).toBe(false)
    expect(localStorage.getItem('hathor:token')).toBe('keep')
    expect(localStorage.getItem('hathor:refresh')).toBe('keep-refresh')
  })

  it('single-flights concurrent refreshes in one tab', async () => {
    localStorage.setItem('hathor:token', 'stale')
    localStorage.setItem('hathor:refresh', 'refresh-1')
    let meCalls = 0
    const calls = mockFetch((url) => {
      if (url === '/api/v1/auth/me') {
        meCalls += 1
        return meCalls <= 2
          ? json({ message: 'Unauthorized' }, 401)
          : json({ userId: 'u', username: 'gabriel' })
      }
      if (url === '/api/v1/auth/refresh') {
        return json({ accessToken: 'fresh', refreshToken: 'refresh-2' })
      }
      throw new Error(`unexpected ${url}`)
    })

    await Promise.all([api.me(), api.me()])
    expect(calls.filter((url) => url.endsWith('/auth/refresh'))).toHaveLength(1)
  })

  it('reuses tokens rotated by another tab instead of replaying the old one', async () => {
    localStorage.setItem('hathor:token', 'stale')
    localStorage.setItem('hathor:refresh', 'refresh-1')
    const requestLock = vi.fn(
      async (_name: string, cb: () => Promise<unknown>) => {
        // Another tab finishes its refresh while this one waits.
        localStorage.setItem('hathor:token', 'other-tab-token')
        localStorage.setItem('hathor:refresh', 'refresh-2')
        return cb()
      },
    )
    Object.defineProperty(navigator, 'locks', {
      configurable: true,
      value: { request: requestLock },
    })
    let meCalls = 0
    const calls = mockFetch((url) => {
      if (url === '/api/v1/auth/me') {
        meCalls += 1
        return meCalls === 1
          ? json({ message: 'Unauthorized' }, 401)
          : json({ userId: 'u', username: 'gabriel' })
      }
      throw new Error(`unexpected ${url}`)
    })

    await expect(api.me()).resolves.toEqual({ userId: 'u', username: 'gabriel' })
    expect(requestLock).toHaveBeenCalledWith('hathor:refresh', expect.any(Function))
    expect(calls.some((url) => url.endsWith('/auth/refresh'))).toBe(false)
    expect(localStorage.getItem('hathor:token')).toBe('other-tab-token')
  })
})
