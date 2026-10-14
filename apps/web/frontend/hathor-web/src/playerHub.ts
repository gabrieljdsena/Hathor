import { useEffect } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { hasInflightTransport, usePlayer } from './store/player'
import type { PlayerState, Song } from './api/client'

// Multi-tab transport sync: every control mutation (toggle, play, next,
// prev, seek, queue edits — from any tab, phone, or headless client) makes
// the server broadcast the full state to this user's group. Applying it
// directly converges all open tabs with zero extra round trips: pausing in
// one tab stops audio in every other tab instead of leaving ghost playback
// behind, and a track change elsewhere moves this tab too. The handler is
// the same set()+syncAudio() pair every server round trip already uses, so
// fade-aware and drift-guarded behavior is unchanged.
export function usePlayerHub(username: string | null) {
  useEffect(() => {
    let token: string | null = null
    try {
      token = localStorage.getItem('hathor:token')
    } catch {
      token = null
    }
    if (!username || !token) return
    let conn: { stop: () => Promise<void> } | null = null
    try {
      const built = new HubConnectionBuilder()
        .withUrl('/hubs/player', {
          accessTokenFactory: () => {
            try {
              return localStorage.getItem('hathor:token') ?? ''
            } catch {
              return ''
            }
          },
        })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build()
      conn = built
      built.on('PlaybackStateChanged', (state: PlayerState) => {
        try {
          if (!state || typeof state !== 'object') return
          // One of OUR OWN transports is mid-flight (overlapping rapid
          // clicks): its REST responses decide the outcome. A broadcast
          // landing now reflects pre-flight server state and would flip us
          // back to the old song — skip it. Genuine remote changes arriving
          // with no local flight in progress always apply.
          if (hasInflightTransport()) return
          // RAM: the queue array is the fattest retained structure (full
          // library on play-all). When its version matches what we hold,
          // apply scalars only and keep our array — no new retained MBs.
          // A version mismatch (reorder, jump, advance elsewhere) or a
          // version reset (service restart) applies the whole state.
          const held = usePlayer.getState()
          if (
            typeof state.queueVersion === 'number' &&
            state.queueVersion === held.queueVersion &&
            state.queueVersion !== 0
          ) {
            const { queue: _dropped, queueTotal: _same, ...scalars } = state
            void _dropped
            void _same
            usePlayer.setState(scalars)
          } else {
            usePlayer.setState(state)
          }
          usePlayer.getState().syncAudio()
        } catch {
          /* a bad payload must never break the socket */
        }
      })
      // QueueUpdated carries the version as a second arg (old servers send
      // one arg — then the version is unknown and the array applies, which
      // also reconverges after a service restart reset it to 0).
      built.on('QueueUpdated', (queue: Song[], version?: number) => {
        try {
          if (Array.isArray(queue)) {
            usePlayer.setState(
              typeof version === 'number' ? { queue, queueVersion: version } : { queue },
            )
          }
        } catch {
          /* ignore */
        }
      })
      built.onreconnected(() => {
        // Broadcasts are fire-and-forget: re-pull once after a drop so a
        // change mid-outage still converges.
        void usePlayer.getState().refresh().catch(() => {})
      })
      void built.start().catch(() => {})
    } catch {
      return
    }
    return () => {
      const c = conn
      conn = null
      if (c) void c.stop().catch(() => {})
    }
  }, [username])
}
