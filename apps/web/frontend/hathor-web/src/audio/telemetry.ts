import { api, type PlaybackEventPayload, type Song } from '../api/client'

// Playback segment telemetry: one report per track play (completed, skipped
// or partial). Kept outside the player store so a failed report can never
// affect audio; the interval only reads state + engine time.
export interface PlaybackTelemetryState {
  currentSong: Song | null
  isPlaying: boolean
}

export interface PlaybackPosition {
  position: number
  duration: number
}

type ActiveSegment = {
  id: string
  file: string
  startedAtMs: number
  lastPosition: number
  lastDuration: number
}

let active: ActiveSegment | null = null
let timer: number | null = null
let pageHideHandler: (() => void) | null = null

function newSegmentId(): string {
  try {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto)
      return crypto.randomUUID()
  } catch {
    /* fall through */
  }
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
}

function finite(value: number, fallback: number): number {
  return Number.isFinite(value) ? value : fallback
}

function closeSegment(
  position: number,
  duration: number,
  forceCompleted: boolean,
  keepalive = false,
) {
  const seg = active
  if (!seg) return
  active = null

  const d = duration > 0 ? duration : seg.lastDuration
  const p = d > 0 ? Math.min(Math.max(0, position), d) : Math.max(0, position)
  // Near-end closes (crossfade/advance) count as completions; manual skips
  // below the halfway point are the only negatives.
  const completed = forceCompleted || (d > 5 && p >= d - 5)
  const skipped = !completed && d > 0 && p < d * 0.5

  const payload: PlaybackEventPayload = {
    segmentId: seg.id,
    file: seg.file,
    positionSec: p,
    durationSec: d,
    completed,
    skipped,
    startedAtUtc: new Date(seg.startedAtMs).toISOString(),
  }
  void api.recordPlaybackEvent(payload, keepalive).catch(() => {
    /* telemetry must never break playback */
  })
}

// The engine's own `ended` callback is exact; use it instead of waiting for
// the next interval tick (which may see the next track already loaded).
export function recordEndedPlayback(position: number, duration: number) {
  if (!active) return
  const d = duration > 0 ? duration : active.lastDuration
  closeSegment(d > 0 ? d : position, d, true)
}

export function flushPlaybackTelemetry(keepalive = true) {
  if (!active) return
  closeSegment(active.lastPosition, active.lastDuration, false, keepalive)
}

export function startPlaybackTelemetry(
  getState: () => PlaybackTelemetryState,
  getPosition: () => PlaybackPosition,
  intervalMs = 1000,
): () => void {
  if (typeof window === 'undefined') return () => {}

  stopPlaybackTelemetry({ flush: false })
  active = null

  const tick = () => {
    const state = getState()
    const song = state.currentSong
    const file = song && !song.isPodcast ? song.file : null

    let pos: PlaybackPosition = { position: 0, duration: 0 }
    try {
      pos = getPosition()
    } catch {
      /* engine read must never break telemetry */
    }
    const position = Math.max(0, finite(pos.position, 0))
    const duration = Math.max(0, finite(pos.duration, song?.duration ?? 0))

    if (active && active.file !== file) {
      closeSegment(active.lastPosition, active.lastDuration, false)
    }

    if (!file) return
    if (!active) {
      if (!state.isPlaying) return
      active = {
        id: newSegmentId(),
        file,
        startedAtMs: Date.now(),
        lastPosition: position,
        lastDuration: duration,
      }
      return
    }

    if (active.file === file) {
      active.lastPosition = position
      if (duration > 0) active.lastDuration = duration
    }
  }

  tick()
  timer = window.setInterval(tick, Math.max(250, intervalMs))
  pageHideHandler = () => flushPlaybackTelemetry(true)
  window.addEventListener('pagehide', pageHideHandler)

  return () => stopPlaybackTelemetry({ flush: true })
}

function stopPlaybackTelemetry({ flush }: { flush: boolean }) {
  if (flush) flushPlaybackTelemetry(true)
  if (timer !== null) {
    window.clearInterval(timer)
    timer = null
  }
  if (pageHideHandler) {
    window.removeEventListener('pagehide', pageHideHandler)
    pageHideHandler = null
  }
}

// Test isolation: clear module state without emitting a report.
export function resetPlaybackTelemetryForTests() {
  stopPlaybackTelemetry({ flush: false })
  active = null
}
