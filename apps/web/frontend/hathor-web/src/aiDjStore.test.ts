import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAiDj } from './store/aiDj'

beforeEach(() => {
  useAiDj.setState({ waiting: false, voicePlaying: false })
})

describe('AiDj store', () => {
  it('does not notify subscribers when waiting does not change', () => {
    const listener = vi.fn()
    const unsubscribe = useAiDj.subscribe(listener)

    useAiDj.getState().setWaiting(false)
    expect(listener).not.toHaveBeenCalled()

    useAiDj.getState().setWaiting(true)
    expect(listener).toHaveBeenCalledTimes(1)

    useAiDj.getState().setWaiting(true)
    expect(listener).toHaveBeenCalledTimes(1)
    unsubscribe()
  })

  it('does not notify subscribers when voice playback does not change', () => {
    const listener = vi.fn()
    const unsubscribe = useAiDj.subscribe(listener)

    useAiDj.getState().setVoicePlaying(false)
    expect(listener).not.toHaveBeenCalled()

    useAiDj.getState().setVoicePlaying(true)
    expect(listener).toHaveBeenCalledTimes(1)

    useAiDj.getState().setVoicePlaying(true)
    expect(listener).toHaveBeenCalledTimes(1)
    unsubscribe()
  })
})
