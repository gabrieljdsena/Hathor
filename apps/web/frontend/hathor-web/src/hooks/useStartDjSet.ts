import { useCallback, useState } from 'react'
import { api } from '../api/client'
import { engine } from '../audio/engine'
import { useAiDj } from '../store/aiDj'
import { releaseDjHold, usePlayer } from '../store/player'

export function useStartDjSet() {
  const [error, setError] = useState<string | null>(null)

  const start = useCallback(async (brief?: string) => {
    setError(null)
    releaseDjHold()
    engine.stopVoice()
    useAiDj.getState().setVoicePlaying(false)
    try {
      const state = await useAiDj.getState().start({
        brief: brief?.trim() || undefined,
      })
      const batch = state.currentBatch
      const first = batch?.tracks[0]
      if (!batch || !first || !state.sessionId) {
        setError('The DJ did not return a playable first set.')
        return false
      }
      await usePlayer.getState().playSongByFiles(
        first,
        batch.tracks.map((track) => track.file),
        { type: 'ai_dj', id: state.sessionId },
      )
      if (batch.shouldComment && batch.introClipIds.length > 0) {
        useAiDj.getState().setVoicePlaying(true)
        engine.playVoice(
          batch.introClipIds.map((id) => api.aiDjClipUrl(id)),
          state.settings.duckLevel,
        )
      }
      return true
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not start the AI DJ.')
      return false
    }
  }, [])

  return { start, error, setError }
}
