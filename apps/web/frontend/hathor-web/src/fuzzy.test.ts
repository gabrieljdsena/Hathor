import { describe, expect, it } from 'vitest'
import { fuzzyFields, fuzzyMatch } from './utils/fuzzy'

describe('fuzzyMatch', () => {
  it.each([
    ['Bohemian Rhapsody Queen', 'bohemian'],
    ['Bohemian Rhapsody Queen', 'Queen Rhapsody'],
    ['Bohemian Rhapsody Queen', 'Bohem'],
    ['Anything', ''],
  ])('matches exact/partial queries (%s ~ %s)', (hay, query) => {
    expect(fuzzyMatch(hay, query)).toBe(true)
  })

  it.each([
    ['Bohemian Rhapsody', 'Bohemain'],
    ['Bohemian Rhapsody', 'Bohemain Rapsody'],
    ['Bohemian Rhapsody', 'Rhapsodyy'],
    ['Bohemian Rhapsody', 'Rhaposdy'],
    ['Bohemian Rhapsody', 'Rahpsody'],
    ['The Beatles', 'Beetles'],
    ['Nothing Else Matters', 'Nothnig Else Maters'],
  ])('tolerates typos (%s ~ %s)', (hay, query) => {
    expect(fuzzyMatch(hay, query)).toBe(true)
  })

  it.each([
    ['Bohemian Rhapsody', 'Stairway to Heaven'],
    ['Bohemian Rhapsody', 'Bhmn'],
    ['Bohemian Rhapsody', 'xyz'],
    ['Bohemian Rhapsody', 'Bohemian Jazz'],
    ['', 'query'],
  ])('rejects non-matches (%s ~ %s)', (hay, query) => {
    expect(fuzzyMatch(hay, query)).toBe(false)
  })

  it('ignores diacritics', () => {
    expect(fuzzyMatch('Beyoncé', 'beyonce')).toBe(true)
  })
})

describe('fuzzyFields', () => {
  it('matches tokens across title/artist/album', () => {
    expect(fuzzyFields(['Bohemian Rhapsody', 'Queen', 'A Night at the Opera'], 'quuen opera')).toBe(true)
  })

  it('returns true for blank queries', () => {
    expect(fuzzyFields(['Song'], '   ')).toBe(true)
  })
})
