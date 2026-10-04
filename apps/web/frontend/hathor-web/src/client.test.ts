import { describe, expect, it, vi } from 'vitest'
import { api } from './api/client'

function mockFetchOnce(body: string, status = 200, contentType = 'application/json') {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(body, { status, headers: { 'Content-Type': contentType } })),
  )
}

describe('api request', () => {
  it('resolves undefined on empty 200 bodies (no res.json crash)', async () => {
    mockFetchOnce('')
    await expect(api.saveLyrics('s.mp3', null, 'la')).resolves.toBeUndefined()
  })

  it('still parses JSON bodies', async () => {
    mockFetchOnce(JSON.stringify({ text: 'sakura' }))
    await expect(api.romanize('x', false)).resolves.toEqual({ text: 'sakura' })
  })

  it('throws the server message on errors', async () => {
    mockFetchOnce(JSON.stringify({ message: 'nope' }), 400)
    await expect(api.saveLyrics('s.mp3', null, 'la')).rejects.toThrow('nope')
  })
})

describe('discover api', () => {
  function captureFetch(body: string) {
    const calls: { input: unknown; init?: RequestInit }[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: unknown, init?: RequestInit) => {
        calls.push({ input, init })
        return new Response(body, { status: 200, headers: { 'Content-Type': 'application/json' } })
      }),
    )
    return calls
  }

  it('GETs /discover', async () => {
    const calls = captureFetch(JSON.stringify({ date: '2026-10-04', items: [], cached: true }))
    await expect(api.discover()).resolves.toEqual({ date: '2026-10-04', items: [], cached: true })
    expect(calls[0].input).toBe('/api/v1/discover')
    expect(calls[0].init?.method).toBeUndefined()
  })

  it('POSTs /discover/refresh', async () => {
    const calls = captureFetch(JSON.stringify({ date: '2026-10-04', items: [], cached: false }))
    await expect(api.refreshDiscover()).resolves.toEqual({
      date: '2026-10-04',
      items: [],
      cached: false,
    })
    expect(calls[0].input).toBe('/api/v1/discover/refresh')
    expect(calls[0].init?.method).toBe('POST')
  })
})
