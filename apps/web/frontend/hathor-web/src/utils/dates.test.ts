import { describe, expect, it } from 'vitest'
import { parseServerDate } from './dates'

// Server instants are UTC: offset-less datetimes must read as UTC, not
// local time (a bare `new Date` would shift every display by the UTC
// offset). Suffixed strings and calendar-day strings pass through.
describe('parseServerDate', () => {
  it('returns null for empty or invalid input', () => {
    expect(parseServerDate(null)).toBeNull()
    expect(parseServerDate('')).toBeNull()
    expect(parseServerDate('not-a-date')).toBeNull()
  })

  it('reads offset-less datetimes as UTC', () => {
    const d = parseServerDate('2026-10-11T14:30:00')
    expect(d?.getTime()).toBe(Date.UTC(2026, 9, 11, 14, 30, 0))
  })

  it('keeps explicit Zulu strings unchanged', () => {
    const d = parseServerDate('2026-10-11T14:30:00Z')
    expect(d?.getTime()).toBe(Date.UTC(2026, 9, 11, 14, 30, 0))
  })

  it('honours explicit numeric offsets', () => {
    const d = parseServerDate('2026-10-11T14:30:00+02:00')
    expect(d?.getTime()).toBe(Date.UTC(2026, 9, 11, 12, 30, 0))
  })

  it('passes calendar-day strings through without mangling them', () => {
    // No caller sends date-only strings today (daily-mix/discover render
    // those themselves); the helper must at least not corrupt them.
    const d = parseServerDate('2026-10-11')
    expect(d?.getTime()).toBe(Date.UTC(2026, 9, 11))
  })
})
