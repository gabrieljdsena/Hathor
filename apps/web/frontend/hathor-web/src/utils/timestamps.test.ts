import { describe, expect, it } from 'vitest'
import {
  activeChapterAt,
  autoSkipTarget,
  formatChapterTime,
  nextChapterStart,
  parseChapterInput,
  prevChapterStart,
  sortChapters,
} from './timestamps'
import type { PodcastTimestamp } from '../api/client'

const chap = (id: number, name: string, startSecs: number, endSecs: number | null = null): PodcastTimestamp => ({
  id,
  podcastFile: 'ep.mp3',
  name,
  startSecs,
  endSecs,
})

describe('formatChapterTime', () => {
  it('formats sub-hour times as m:ss', () => {
    expect(formatChapterTime(0)).toBe('0:00')
    expect(formatChapterTime(90)).toBe('1:30')
    expect(formatChapterTime(59.9)).toBe('0:59')
  })

  it('formats hour+ times as h:mm:ss', () => {
    expect(formatChapterTime(3723)).toBe('1:02:03')
  })
})

describe('parseChapterInput', () => {
  it('accepts bare seconds', () => {
    expect(parseChapterInput('90')).toBe(90)
    expect(parseChapterInput('  7.5 ')).toBe(7.5)
  })

  it('accepts m:ss and h:mm:ss', () => {
    expect(parseChapterInput('1:30')).toBe(90)
    expect(parseChapterInput('01:02:03')).toBe(3723)
  })

  it('rejects garbage and out-of-range parts', () => {
    expect(parseChapterInput('')).toBeNull()
    expect(parseChapterInput('abc')).toBeNull()
    expect(parseChapterInput('-5')).toBeNull()
    expect(parseChapterInput('1:99')).toBeNull()
    expect(parseChapterInput('1:2:3:4')).toBeNull()
  })
})

describe('activeChapterAt', () => {
  const chapters = [chap(1, 'Intro', 0, 60), chap(2, 'Main', 60), chap(3, 'Outro', 600, 660)]

  it('picks the last chapter at or before t', () => {
    expect(activeChapterAt(chapters, 0)?.name).toBe('Intro')
    expect(activeChapterAt(chapters, 61)?.name).toBe('Main')
    expect(activeChapterAt(chapters, 1000)?.name).toBe('Outro')
  })

  it('treats the end bound as display-only', () => {
    // Past Outro's explicit end the last-started chapter still owns the position.
    expect(activeChapterAt(chapters, 700)?.name).toBe('Outro')
  })
})

describe('sortChapters', () => {
  it('orders by start then id', () => {
    const out = sortChapters([chap(3, 'B', 60), chap(1, 'A', 0), chap(2, 'C', 60)])
    expect(out.map((c) => c.name)).toEqual(['A', 'C', 'B'])
  })
})

describe('nextChapterStart', () => {
  const chapters = [chap(1, 'Intro', 0), chap(2, 'Main', 60), chap(3, 'Outro', 600)]

  it('finds the first boundary past the position', () => {
    expect(nextChapterStart(chapters, 0)).toBe(60)
    expect(nextChapterStart(chapters, 61)).toBe(600)
  })

  it('returns null past the last chapter', () => {
    expect(nextChapterStart(chapters, 600)).toBeNull()
    expect(nextChapterStart(chapters, 9999)).toBeNull()
  })
})

describe('prevChapterStart', () => {
  const chapters = [chap(1, 'Intro', 0), chap(2, 'Main', 60), chap(3, 'Outro', 600)]

  it('restarts the chapter when well inside it', () => {
    expect(prevChapterStart(chapters, 100)).toBe(60)
  })

  it('jumps to the previous chapter near a boundary', () => {
    expect(prevChapterStart(chapters, 61)).toBe(0)
    expect(prevChapterStart(chapters, 600)).toBe(60)
  })

  it('returns null at the very start', () => {
    expect(prevChapterStart(chapters, 0)).toBeNull()
    expect(prevChapterStart(chapters, 2)).toBeNull()
  })
})

describe('autoSkipTarget', () => {
  it('starts from the first chapter instead of 0:00', () => {
    const chapters = [chap(1, 'Intro', 30), chap(2, 'Main', 90)]
    expect(autoSkipTarget(chapters, 0)).toBe(30)
    expect(autoSkipTarget(chapters, 10)).toBe(30)
    // Already there (or near enough): stable, no re-trigger.
    expect(autoSkipTarget(chapters, 30)).toBeNull()
    expect(autoSkipTarget(chapters, 29.8)).toBeNull()
  })

  it('leaves a first chapter at the very start alone', () => {
    expect(autoSkipTarget([chap(1, 'Intro', 0), chap(2, 'Main', 60)], 0)).toBeNull()
  })

  it('lets a segment play in full, then jumps the gap into the next start', () => {
    const chapters = [chap(1, 'Intro', 0), chap(2, 'Ad', 60, 90), chap(3, 'Main', 120)]
    // Mid-segment: no skip — the chapter plays to its end time.
    expect(autoSkipTarget(chapters, 70)).toBeNull()
    // At/past the end: jump into the next chapter.
    expect(autoSkipTarget(chapters, 90)).toBe(120)
    expect(autoSkipTarget(chapters, 95)).toBe(120)
    // Landed: stable, no re-trigger.
    expect(autoSkipTarget(chapters, 120)).toBeNull()
  })

  it('does nothing past the end with no later chapter', () => {
    const chapters = [chap(1, 'Main', 0), chap(2, 'Outro', 600, 660)]
    expect(autoSkipTarget(chapters, 610)).toBeNull()
    expect(autoSkipTarget(chapters, 700)).toBeNull()
  })

  it('ignores open-ended chapters and adjacent ones', () => {
    const chapters = [chap(1, 'Intro', 0), chap(2, 'Main', 60)]
    expect(autoSkipTarget(chapters, 10)).toBeNull()
    expect(autoSkipTarget(chapters, 100)).toBeNull()
    // Next chapter starts at/past the end: natural playback covers it.
    const adjacent = [chap(1, 'A', 60, 90), chap(2, 'B', 90)]
    expect(autoSkipTarget(adjacent, 90)).toBeNull()
    expect(autoSkipTarget(adjacent, 95)).toBeNull()
  })
})
