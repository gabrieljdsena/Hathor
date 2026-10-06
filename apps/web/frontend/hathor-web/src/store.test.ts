import { describe, expect, it, vi } from 'vitest'
import { engine } from './audio/engine'
import { usePlayer } from './store/player'

const { mockUpdateSettings } = vi.hoisted(() => ({ mockUpdateSettings: vi.fn() }))

vi.mock('./api/client', async (importOriginal) => {
  const mod = await importOriginal<typeof import('./api/client')>()
  return {
    ...mod,
    api: { ...mod.api, updateSettings: (...args: unknown[]) => mockUpdateSettings(...args) },
  }
})

describe('normalize preference (browser-local)', () => {
  it('persists to localStorage and drives the engine', () => {
    usePlayer.getState().setNormalize(true)
    expect(localStorage.getItem('hathor:normalize')).toBe('1')
    expect(engine.isNormalize()).toBe(true)

    usePlayer.getState().setNormalize(false)
    expect(localStorage.getItem('hathor:normalize')).toBe('0')
    expect(engine.isNormalize()).toBe(false)
  })
})

describe('chapter skip preference (local + account-wide)', () => {
  it('persists to localStorage and to server settings', () => {
    mockUpdateSettings.mockResolvedValue({})
    usePlayer.getState().setChapterSkip(true)
    expect(usePlayer.getState().chapterSkip).toBe(true)
    expect(localStorage.getItem('hathor:chapterskip')).toBe('1')
    expect(mockUpdateSettings).toHaveBeenCalledWith({ chapterSkip: true })

    usePlayer.getState().setChapterSkip(false)
    expect(usePlayer.getState().chapterSkip).toBe(false)
    expect(mockUpdateSettings).toHaveBeenCalledWith({ chapterSkip: false })
  })
})
