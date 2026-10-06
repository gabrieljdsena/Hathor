import type { PodcastTimestamp } from '../api/client'

// Chapter-time helpers for podcast timestamps. Media offsets are plain
// seconds on the wire; the UI shows m:ss / h:mm:ss and accepts the same
// shapes (plus bare seconds) in the editor.

export function formatChapterTime(totalSecs: number): string {
  const s = Math.max(0, Math.floor(totalSecs || 0))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

// Accepts "90", "1:30", "01:02:30" (whitespace tolerated). Returns seconds
// or null when the text is not a valid non-negative time.
export function parseChapterInput(text: string): number | null {
  const t = text.trim()
  if (t.length === 0) return null
  if (/^\d+(\.\d+)?$/.test(t)) {
    const v = Number.parseFloat(t)
    return Number.isFinite(v) && v >= 0 ? v : null
  }
  const parts = t.split(':')
  if (parts.length < 2 || parts.length > 3) return null
  if (!parts.every((p) => /^\d+(\.\d+)?$/.test(p.trim()))) return null
  const nums = parts.map((p) => Number.parseFloat(p.trim()))
  const sec = nums[nums.length - 1]
  const min = nums[nums.length - 2]
  const hour = nums.length === 3 ? nums[0] : 0
  if (min >= 60 || sec >= 60) return null
  const total = hour * 3600 + min * 60 + sec
  return Number.isFinite(total) && total >= 0 ? total : null
}

// Active chapter at position t: the last chapter starting at or before t
// (YouTube-chapters rule). An explicit end is display-only — playback past
// it still belongs to the last-started chapter.
export function activeChapterAt(
  chapters: readonly PodcastTimestamp[],
  t: number,
): PodcastTimestamp | null {
  let active: PodcastTimestamp | null = null
  for (const c of chapters) {
    if (t < c.startSecs) break
    active = c
  }
  return active
}

export function sortChapters(chapters: readonly PodcastTimestamp[]): PodcastTimestamp[] {
  return [...chapters].sort((a, b) => a.startSecs - b.startSecs || a.id - b.id)
}

// Next chapter boundary after position t (small epsilon so the current
// boundary never re-targets itself). Null = no later chapter.
export function nextChapterStart(
  chapters: readonly PodcastTimestamp[],
  t: number,
): number | null {
  for (const c of chapters) {
    if (c.startSecs > t + 0.5) return c.startSecs
  }
  return null
}

// Previous chapter boundary (YouTube rule): more than a few seconds into a
// chapter restarts it, otherwise jumps to the previous chapter's start.
// Null = already at the first chapter.
export function prevChapterStart(
  chapters: readonly PodcastTimestamp[],
  t: number,
): number | null {
  const RESTART_SECS = 3
  let current: PodcastTimestamp | null = null
  let previous: PodcastTimestamp | null = null
  for (const c of chapters) {
    if (c.startSecs > t + 0.25) break
    previous = current
    current = c
  }
  if (!current) return null
  if (t - current.startSecs > RESTART_SECS) return current.startSecs
  return previous ? previous.startSecs : null
}

// Auto-skip landing for position t (chapters must be start-sorted): a
// chapter with an explicit end time plays in full, then playback jumps the
// gap into the next chapter's start. Playback before the first chapter
// jumps straight to it instead of starting from 0:00. Open-ended chapters
// are plain markers, adjacent/overlapping chapters and a trailing chapter
// need no jump — natural playback covers them. Null = nothing to skip.
export function autoSkipTarget(
  chapters: readonly PodcastTimestamp[],
  t: number,
): number | null {
  if (chapters.length === 0) return null
  const first = chapters[0].startSecs
  if (t < first - 0.5) return first
  for (let i = 0; i < chapters.length; i++) {
    const c = chapters[i]
    if (c.endSecs === null || c.endSecs === undefined || c.endSecs <= c.startSecs) continue
    const next = chapters[i + 1]
    if (!next || next.startSecs <= c.endSecs) continue
    if (t >= c.endSecs && t < next.startSecs) return next.startSecs
  }
  return null
}
