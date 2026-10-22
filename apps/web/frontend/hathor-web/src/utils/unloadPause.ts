// Pause-on-unload via keepalive fetch (deliberately NOT sendBeacon):
// unlike media elements, JS can set Authorization headers during unload,
// so the token stays out of URLs and request bodies entirely — standard
// Bearer auth against the existing endpoint, zero backend change, zero new
// token-exposure surface. Best-effort by nature (a killed tab may never
// run it); skips when nothing is playing or no token is stored.
// A second keepalive snapshots this browser's resume spot (device id +
// file + position) so other devices can offer it — same fire-and-forget
// terms as the pause itself.
// NOTE: pagehide only, never visibilitychange — background tabs must keep
// playing while the phone is locked or another tab is focused.
export interface UnloadPauseState {
  isPlaying: boolean
  hasTrack: boolean
  file?: string | null
  positionSec?: number
  isPodcast?: boolean
  deviceId?: string | null
  deviceLabel?: string | null
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
    const position =
      typeof state.positionSec === 'number' && Number.isFinite(state.positionSec)
        ? Math.max(0, state.positionSec)
        : null
    try {
      // Send the exact frozen position: by the time a keepalive lands the
      // server's wall-clock estimate can have run past the song end, which
      // would make the next resume skip the track instead of continuing it.
      post('/api/v1/player/pause', {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ file: state.file ?? null, positionSec: position }),
        keepalive: true,
      })
    } catch {
      /* dying page: nothing more to do */
    }
    // Closing mid-song pauses server-side via the beacon above; snapshot
    // the spot too so the pause position isn't lost to other devices.
    try {
      if (state.file && state.deviceId) {
        post('/api/v1/sync/playback', {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${token}`,
            'Content-Type': 'application/json',
          },
          body: JSON.stringify({
            deviceId: state.deviceId,
            deviceLabel: state.deviceLabel ?? null,
            file: state.file,
            positionSec: Math.max(0, state.positionSec ?? 0),
            isPodcast: state.isPodcast ?? false,
          }),
          keepalive: true,
        })
      }
    } catch {
      /* dying page: nothing more to do */
    }
  }
  window.addEventListener('pagehide', onPageHide)
  return () => window.removeEventListener('pagehide', onPageHide)
}
