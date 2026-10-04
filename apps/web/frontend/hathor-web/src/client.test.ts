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
