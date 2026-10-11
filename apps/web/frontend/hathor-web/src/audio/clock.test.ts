import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { subscribeAudioClock, useAudioClock } from './clock'

const { mockTime } = vi.hoisted(() => ({ mockTime: vi.fn() }))

vi.mock('./engine', () => ({
  engine: {
    time: (...args: unknown[]) => mockTime(...args),
  },
}))

beforeEach(() => {
  vi.useFakeTimers()
  mockTime.mockReturnValue(0)
})

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
})

describe('subscribeAudioClock', () => {
  it('shares one interval across subscribers of the same cadence', () => {
    const spy = vi.spyOn(window, 'setInterval')
    const a = vi.fn()
    const b = vi.fn()
    const stopA = subscribeAudioClock(500, a)
    const stopB = subscribeAudioClock(500, b)
    expect(spy).toHaveBeenCalledTimes(1)

    vi.advanceTimersByTime(500)
    expect(a).toHaveBeenCalledTimes(1)
    expect(b).toHaveBeenCalledTimes(1)

    stopA()
    stopB()
    spy.mockRestore()
  })

  it('uses separate intervals per cadence and clears when empty', () => {
    const setSpy = vi.spyOn(window, 'setInterval')
    const clearSpy = vi.spyOn(window, 'clearInterval')
    const stopFast = subscribeAudioClock(250, vi.fn())
    const stopSlow = subscribeAudioClock(500, vi.fn())
    expect(setSpy).toHaveBeenCalledTimes(2)

    stopFast()
    stopSlow()
    expect(clearSpy).toHaveBeenCalledTimes(2)
    setSpy.mockRestore()
    clearSpy.mockRestore()
  })
})

describe('useAudioClock', () => {
  it('reads the engine on mount and updates on ticks', () => {
    mockTime.mockReturnValue(61)
    const { result } = renderHook(() => useAudioClock(500))
    expect(result.current).toBe(61)

    mockTime.mockReturnValue(62)
    act(() => {
      vi.advanceTimersByTime(500)
    })
    expect(result.current).toBe(62)
  })
})
