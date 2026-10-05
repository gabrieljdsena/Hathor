import { describe, expect, it } from 'vitest'
import { engine } from './audio/engine'
import { usePlayer } from './store/player'

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
