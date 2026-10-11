import { useEffect, useState } from 'react'
import { engine } from './engine'

// Shared playback clock: every lyrics/chapter view used to poll
// engine.time() on its own 250-500ms timer (up to 7 concurrent clocks,
// several re-rendering React on each tick). Now there is one interval per
// cadence no matter how many subscribers. First render still reads the
// engine synchronously (mount-time correctness), ticks update after that.
// The engine's own 200ms fade/watchdog tick stays internal; conditional
// one-offs (ffmpeg poll) stay local too.

interface Bucket {
  listeners: Set<() => void>
  timer: number | undefined
}

const buckets = new Map<number, Bucket>()

function readTime(): number {
  try {
    return engine.time()
  } catch {
    return 0
  }
}

// Non-React subscription for ref-only tickers (progress slider, auto-skip
// pump): DOM writes / engine seeks without re-rendering anything.
export function subscribeAudioClock(ms: number, notify: () => void): () => void {
  let b = buckets.get(ms)
  if (!b) {
    b = { listeners: new Set(), timer: undefined }
    buckets.set(ms, b)
  }
  b.listeners.add(notify)
  if (b.timer === undefined) {
    const bucket = b
    bucket.timer = window.setInterval(() => {
      bucket.listeners.forEach((listener) => {
        try {
          listener()
        } catch {
          // A dead subscriber must never kill the shared clock.
        }
      })
    }, ms)
  }
  return () => {
    b.listeners.delete(notify)
    if (b.listeners.size === 0 && b.timer !== undefined) {
      window.clearInterval(b.timer)
      b.timer = undefined
    }
  }
}

// React subscription: re-renders the caller on each tick with the current
// media time. Cadence is per call site (lyric highlight 250ms, chapters
// 500ms); same-cadence callers share one interval.
export function useAudioClock(ms = 500): number {
  const [now, setNow] = useState(readTime)
  useEffect(() => subscribeAudioClock(ms, () => setNow(readTime())), [ms])
  return now
}
