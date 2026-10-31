import { create } from 'zustand'
import { api, type AiDjStartPayload, type AiDjState } from '../api/client'

interface AiDjStore {
  state: AiDjState | null
  loading: boolean
  voicePlaying: boolean
  waiting: boolean
  refresh: () => Promise<void>
  setState: (state: AiDjState | null) => void
  start: (payload: AiDjStartPayload) => Promise<AiDjState>
  stop: () => Promise<void>
  feedback: (event: string, trackFile?: string | null) => Promise<void>
  setVoicePlaying: (playing: boolean) => void
  setWaiting: (waiting: boolean) => void
}

export const useAiDj = create<AiDjStore>((set, get) => ({
  state: null,
  loading: false,
  voicePlaying: false,
  waiting: false,

  refresh: async () => {
    try {
      const state = await api.aiDjState()
      set({ state })
    } catch {
      // Offline/disabled must not disturb playback.
    }
  },

  setState: (state) => set({ state }),

  start: async (payload) => {
    set({ loading: true })
    try {
      const state = await api.aiDjStart(payload)
      set({ state, loading: false })
      return state
    } catch (e) {
      set({ loading: false })
      throw e
    }
  },

  stop: async () => {
    try {
      const state = await api.aiDjStop()
      set({ state, voicePlaying: false, waiting: false })
    } catch {
      set({ voicePlaying: false, waiting: false })
    }
  },

  feedback: async (event, trackFile = null) => {
    const state = get().state
    if (!state?.sessionId) return
    await api
      .aiDjFeedback({
        sessionId: state.sessionId,
        batchId: state.currentBatch?.id ?? null,
        trackFile,
        event,
      })
      .catch(() => {})
  },

  setVoicePlaying: (playing) => set({ voicePlaying: playing }),
  setWaiting: (waiting) => set({ waiting }),
}))
