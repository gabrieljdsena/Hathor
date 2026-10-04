import { describe, expect, it } from 'vitest'
import { activeLrcIndex, formatDuration, parseLrc } from './lyrics'

describe('parseLrc', () => {
  it('parses timestamps and sorts by time', () => {
    const lines = parseLrc('[00:20.00] second\n[00:10.50] first\nno stamp\n[bad] x')
    expect(lines).toHaveLength(2)
    expect(lines[0]).toEqual({ timeSec: 10.5, text: 'first' })
    expect(lines[1]).toEqual({ timeSec: 20, text: 'second' })
  })

  it('returns empty for null input', () => {
    expect(parseLrc(null)).toEqual([])
  })
})

describe('activeLrcIndex', () => {
  const lines = parseLrc('[00:10.00] a\n[00:20.00] b\n[00:30.00] c')

  it('picks the last line at or before the position', () => {
    expect(activeLrcIndex(lines, 0)).toBe(-1)
    expect(activeLrcIndex(lines, 10)).toBe(0)
    expect(activeLrcIndex(lines, 25)).toBe(1)
    expect(activeLrcIndex(lines, 99)).toBe(2)
  })
})

describe('formatDuration', () => {
  it('formats m:ss', () => {
    expect(formatDuration(0)).toBe('0:00')
    expect(formatDuration(65)).toBe('1:05')
    expect(formatDuration(3600)).toBe('60:00')
  })
})
