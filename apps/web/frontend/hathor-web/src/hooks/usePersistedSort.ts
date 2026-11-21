import { useCallback, useEffect, useState } from 'react'

export type SortDirection = 'asc' | 'desc'

export const SORT_STORAGE = {
  songs: 'hathor:sort:songs',
  artist: 'hathor:sort:artist',
  album: 'hathor:sort:album',
  playlist: 'hathor:sort:playlist',
  mix: 'hathor:sort:mix',
} as const

export interface PersistedSort<TKey extends string> {
  sort: TKey | null
  dir: SortDirection
  setSort: (sort: TKey | null, dir: SortDirection) => void
}

function readSort<TKey extends string>(
  storageKey: string | null,
  allowed: readonly TKey[],
): { sort: TKey | null; dir: SortDirection } {
  if (!storageKey || typeof localStorage === 'undefined') {
    return { sort: null, dir: 'asc' }
  }
  try {
    const raw = localStorage.getItem(storageKey)
    if (!raw) return { sort: null, dir: 'asc' }
    const parsed = JSON.parse(raw) as { sort?: unknown; dir?: unknown }
    const sort =
      typeof parsed.sort === 'string' &&
      (allowed as readonly string[]).includes(parsed.sort)
        ? (parsed.sort as TKey)
        : null
    return { sort, dir: parsed.dir === 'desc' ? 'desc' : 'asc' }
  } catch {
    return { sort: null, dir: 'asc' }
  }
}

export function usePersistedSort<TKey extends string>(
  storageKey: string | null,
  allowed: readonly TKey[],
): PersistedSort<TKey> {
  const [state, setState] = useState(() => readSort(storageKey, allowed))

  useEffect(() => {
    if (!storageKey || typeof localStorage === 'undefined') return
    try {
      localStorage.setItem(storageKey, JSON.stringify(state))
    } catch {
      // Private mode/storage quota: in-memory sorting still works.
    }
  }, [storageKey, state])

  useEffect(() => {
    if (!storageKey || typeof window === 'undefined') return
    const onStorage = (event: StorageEvent) => {
      if (event.key === storageKey) setState(readSort(storageKey, allowed))
    }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
  }, [storageKey, allowed])

  const setSort = useCallback(
    (sort: TKey | null, dir: SortDirection) => setState({ sort, dir }),
    [],
  )

  return { ...state, setSort }
}
