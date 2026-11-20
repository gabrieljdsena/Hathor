import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import { SORT_STORAGE, usePersistedSort } from './hooks/usePersistedSort'

const KEYS = ['Title', 'Album', 'Duration', 'DateDownload'] as const

beforeEach(() => {
  localStorage.clear()
})

describe('usePersistedSort', () => {
  it('restores the saved header ordering after remount', () => {
    const first = renderHook(() =>
      usePersistedSort(SORT_STORAGE.songs, KEYS),
    )
    expect(first.result.current.sort).toBeNull()
    expect(first.result.current.dir).toBe('asc')

    act(() => {
      first.result.current.setSort('DateDownload', 'desc')
    })
    expect(first.result.current.sort).toBe('DateDownload')
    expect(first.result.current.dir).toBe('desc')
    expect(JSON.parse(localStorage.getItem(SORT_STORAGE.songs)!)).toEqual({
      sort: 'DateDownload',
      dir: 'desc',
    })

    first.unmount()
    const second = renderHook(() =>
      usePersistedSort(SORT_STORAGE.songs, KEYS),
    )
    expect(second.result.current.sort).toBe('DateDownload')
    expect(second.result.current.dir).toBe('desc')
  })

  it('ignores invalid stored values', () => {
    localStorage.setItem(
      SORT_STORAGE.songs,
      JSON.stringify({ sort: 'NotAColumn', dir: 'sideways' }),
    )
    const { result } = renderHook(() =>
      usePersistedSort(SORT_STORAGE.songs, KEYS),
    )
    expect(result.current.sort).toBeNull()
    expect(result.current.dir).toBe('asc')
  })

  it('does not persist when the table has no storage key', () => {
    const { result } = renderHook(() => usePersistedSort(null, KEYS))
    act(() => {
      result.current.setSort('Title', 'desc')
    })
    expect(result.current.sort).toBe('Title')
    expect(localStorage.length).toBe(0)
  })
})
