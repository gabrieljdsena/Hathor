// LRC parsing + active-line lookup for the lyrics sheet.
// Timestamps [mm:ss.xx]; active line = last line at or before position.
export interface LrcLine {
  timeSec: number
  text: string
}

const LRC_LINE = /^\[(\d+):(\d+(?:\.\d+)?)\]\s?(.*)$/

export function parseLrc(synced: string | null): LrcLine[] {
  if (!synced) return []
  const out: LrcLine[] = []
  for (const raw of synced.split('\n')) {
    const m = LRC_LINE.exec(raw.trim())
    if (!m) continue
    const timeSec = Number.parseInt(m[1], 10) * 60 + Number.parseFloat(m[2])
    if (!Number.isFinite(timeSec)) continue
    out.push({ timeSec, text: m[3] })
  }
  return out.sort((a, b) => a.timeSec - b.timeSec)
}

export function activeLrcIndex(lines: LrcLine[], positionSec: number): number {
  let idx = -1
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].timeSec <= positionSec + 0.15) idx = i
    else break
  }
  return idx
}

export function formatDuration(totalSecs: number): string {
  const s = Math.max(0, Math.floor(totalSecs || 0))
  const m = Math.floor(s / 60)
  return `${m}:${String(s % 60).padStart(2, '0')}`
}
