// Pause-on-unload via keepalive fetch (deliberately NOT sendBeacon):
// unlike media elements, JS can set Authorization headers during unload,
// so the token stays out of URLs and request bodies entirely — standard
// Bearer auth against the existing endpoint, zero backend change, zero new
// token-exposure surface. Best-effort by nature (a killed tab may never
// run it); skips when nothing is playing or no token is stored.
// NOTE: pagehide only, never visibilitychange — background tabs must keep
// playing while the phone is locked or another tab is focused.
export interface UnloadPauseState {
  isPlaying: boolean
  hasTrack: boolean
}

export function createUnloadPause(
  readState: () => UnloadPauseState,
  readToken: () => string | null,
  post: (url: string, init: RequestInit) => void = (url, init) => {
    void fetch(url, init)
  },
): () => void {
  const onPageHide = () => {
    let state: UnloadPauseState
    try {
      state = readState()
    } catch {
      return
    }
    if (!state.isPlaying || !state.hasTrack) return
    let token: string | null
    try {
      token = readToken()
    } catch {
      return
    }
    if (!token) return
    try {
      post('/api/v1/player/pause', {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
        keepalive: true,
      })
    } catch {
      /* dying page: nothing more to do */
    }
  }
  window.addEventListener('pagehide', onPageHide)
  return () => window.removeEventListener('pagehide', onPageHide)
}
