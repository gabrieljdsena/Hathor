import { useEffect } from 'react'
import { useAiDj } from './store/aiDj'
import { usePlayer } from './store/player'

export function useAiDjPolling() {
  const status = useAiDj((s) => s.state?.status)

  useEffect(() => {
    void useAiDj.getState().refresh()
  }, [])

  useEffect(() => {
    if (status !== 'active') return
    const timer = window.setInterval(() => {
      void useAiDj.getState().refresh()
    }, 10000)
    const unsubscribe = usePlayer.subscribe((player, previous) => {
      if (player.currentSong?.file !== previous.currentSong?.file) {
        void useAiDj.getState().refresh()
      }
    })
    return () => {
      window.clearInterval(timer)
      unsubscribe()
    }
  }, [status])
}
