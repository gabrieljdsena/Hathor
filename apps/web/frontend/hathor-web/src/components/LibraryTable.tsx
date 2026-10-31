import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type QueueSource, type Song } from '../api/client'
import { usePlayer } from '../store/player'
import SearchInput, { useDebouncedValue } from './ui/SearchInput'
import { fuzzyFields } from '../utils/fuzzy'
import Icon from './ui/icons'
import SongMenu, { type SongMenuHandle } from './ui/SongMenu'
import SongRow, { SONG_ROW_GRID } from './ui/SongRow'
import SortableHeader, { type SortColumn } from './ui/SortableHeader'
import { EmptyState, LoadingState } from './ui/states'

export type SortKey = 'Title' | 'Album' | 'Duration' | 'DateDownload'

const COLUMNS: Array<SortColumn<SortKey>> = [
  { key: 'Title', label: 'Song' },
  { key: 'Album', label: 'Album', hideOnMobile: true },
  { key: 'Duration', label: 'Duration', align: 'right' },
  { key: 'DateDownload', label: 'Added', align: 'right', hideOnMobile: true },
]

// Per-row remove-from-playlist affordance (playlist detail only).
export function RemoveFromPlaylistButton({ onRemove }: { onRemove: () => void }) {
  return (
    <button
      onClick={(e) => {
        e.stopPropagation()
        onRemove()
      }}
      title="Remove from playlist"
      className="p-1.5 rounded-full text-zinc-600 hover:text-red-300 hover:bg-white/10 transition-all cursor-pointer flex-shrink-0"
    >
      <Icon name="x" className="w-4 h-4 pointer-events-none" />
    </button>
  )
}

// Resolve a Song for a file from loaded pages, else a single fetch
// (play-all/shuffle picks a file the table may not have loaded yet).
export async function resolveSong(pages: Song[][] | undefined, file: string): Promise<Song> {
  for (const page of pages ?? []) {
    const hit = page.find((s) => s.file === file)
    if (hit) return hit
  }
  return api.song(file)
}

interface SharedProps {
  isLoading: boolean
  loadingLabel?: string
  emptyTitle?: string
  emptyHint?: string
  source: QueueSource
  searchPlaceholder?: string
  searchId?: string
  onRemoveFromPlaylist?: (song: Song) => void
}

interface LegacyProps extends SharedProps {
  songs: Song[] | undefined
}

interface PagedProps extends SharedProps {
  songs?: undefined
  pages: Song[][] | undefined
  total: number
  isFetchingNextPage: boolean
  isFetchingPreviousPage: boolean
  hasNextPage: boolean
  hasPreviousPage: boolean
  fetchNextPage: () => void
  fetchPreviousPage: () => void
  // Controlled search/sort (owned by the parent query key).
  search: string
  onSearchChange: (value: string) => void
  sort: SortKey | null
  dir: 'asc' | 'desc'
  onSortChange: (sort: SortKey | null, dir: 'asc' | 'desc') => void
  // Ordered file names for the queue (play uses playSongByFiles so the
  // full Song[] list is never materialized).
  getFiles: () => Promise<string[]>
}

type Props = LegacyProps | PagedProps

// Shared searchable/sortable song table (Songs, Playlist detail,
// Artist/Album detail). Legacy mode holds the whole list with client
// filter/sort (Mix ≤50 songs, context-menu tests); paged mode renders
// server pages with infinite scroll.
export default function LibraryTable(props: Props) {
  const {
    isLoading,
    loadingLabel = 'Loading…',
    emptyTitle = 'No songs yet',
    emptyHint,
    source,
    searchPlaceholder = 'Search...',
    searchId,
    onRemoveFromPlaylist,
  } = props
  // `in`-based narrowing (LegacyProps declares no `pages`, so this
  // discriminates the union for both TS and runtime).
  const pp = 'pages' in props ? props : null
  const lp = 'pages' in props ? null : props
  const playSong = usePlayer((s) => s.playSong)
  const playSongByFiles = usePlayer((s) => s.playSongByFiles)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const isPlaying = usePlayer((s) => s.isPlaying)
  const navigate = useNavigate()
  const [legacySearch, setLegacySearch] = useState('')
  const legacyDebounced = useDebouncedValue(legacySearch)
  const [legacySort, setLegacySort] = useState<SortKey | null>(null)
  const [legacyDir, setLegacyDir] = useState<'asc' | 'desc'>('asc')
  const menuRefs = useRef(new Map<string, SongMenuHandle | null>())
  const openMenu = (file: string, x: number, y: number) =>
    menuRefs.current.get(file)?.openAt(x, y)

  // Typo-tolerant client filter (mirrors the API ?search= fuzzy match),
  // memoized: the fuzzy pass over thousands of rows runs only when its
  // inputs change, not on every parent/store render. Legacy mode only —
  // paged mode filters server-side.
  const legacyVisible = useMemo(() => {
    if (!lp) return []
    const list = (lp.songs ?? []).filter(
      (s) => legacyDebounced.trim().length === 0 || fuzzyFields([s.title, s.artist, s.album], legacyDebounced),
    )
    if (!legacySort) return list
    const d = legacyDir === 'asc' ? 1 : -1
    return [...list].sort((a, b) => {
      if (legacySort === 'Duration') return (a.duration - b.duration) * d
      if (legacySort === 'DateDownload') {
        // ISO timestamps sort lexicographically; missing dates go last.
        const av = a.dateDownload ?? ''
        const bv = b.dateDownload ?? ''
        if (!av) return 1
        if (!bv) return -1
        return av < bv ? -d : av > bv ? d : 0
      }
      const av = (a[legacySort === 'Title' ? 'title' : 'album'] ?? '').toLowerCase()
      const bv = (b[legacySort === 'Title' ? 'title' : 'album'] ?? '').toLowerCase()
      return av < bv ? -d : av > bv ? d : 0
    })
  }, [lp, legacyDebounced, legacySort, legacyDir])

  // Pages identity comes from React Query (stable across parent renders);
  // key the memo on it, not on the props object (new identity each render).
  const isPaged = pp !== null
  const pagesProp = pp?.pages
  const flattened = useMemo(
    () => (isPaged ? (pagesProp ?? []).flat() : legacyVisible),
    [isPaged, pagesProp, legacyVisible],
  )
  const searchValue = pp ? pp.search : legacySearch
  const setSearchValue = pp ? pp.onSearchChange : setLegacySearch

  // RAM windowing: `flattened` is everything loaded, but only the first
  // `renderLimit` rows mount. A sentinel at the bottom grows the window on
  // scroll — a 10k library mounts ~150 rows instead of 10k SongRow +
  // SongMenu + CoverArt trees. Paged mode chains the sentinel into
  // fetchNextPage once the loaded rows are exhausted.
  const PAGE = 150
  const [renderLimit, setRenderLimit] = useState(PAGE)
  const sentinelRef = useRef<HTMLDivElement | null>(null)
  const topSentinelRef = useRef<HTMLDivElement | null>(null)
  // Scroll anchor for prepended pages (maxPages backward fetch): the
  // viewport position of the top sentinel before the fetch, restored
  // after commit so the list doesn't jump when older rows mount above.
  const anchorRef = useRef<number | null>(null)
  const prevLenRef = useRef(0)
  // New filter/sort/search resets the window to the top. Pages arriving
  // must NOT reset it (that would collapse the list on every fetch).
  const resetKey = pp
    ? `${pp.search}|${pp.sort}|${pp.dir}`
    : `${legacyDebounced}|${legacySort}|${legacyDir}`
  useEffect(() => {
    setRenderLimit(PAGE)
  }, [resetKey])

  // Drop menu handles for rows that no longer exist (unbounded Map growth
  // across searches otherwise).
  useEffect(() => {
    const alive = new Set(flattened.map((s) => s.file))
    for (const key of menuRefs.current.keys()) {
      if (!alive.has(key)) menuRefs.current.delete(key)
    }
  }, [flattened])

  // Grow the render window when the sentinel scrolls near; paged mode
  // fetches the next server page once everything loaded is rendered.
  const rendered = flattened.slice(0, renderLimit)
  const hasNextPage = pp?.hasNextPage ?? false
  const isFetchingNextPage = pp?.isFetchingNextPage ?? false
  const fetchNextPage = pp?.fetchNextPage
  const hasPreviousPage = pp?.hasPreviousPage ?? false
  const isFetchingPreviousPage = pp?.isFetchingPreviousPage ?? false
  const fetchPreviousPage = pp?.fetchPreviousPage
  useEffect(() => {
    if (rendered.length >= flattened.length && !hasNextPage) return
    const el = sentinelRef.current
    if (!el || typeof IntersectionObserver === 'undefined') return
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          if (rendered.length < flattened.length) {
            setRenderLimit((n) => Math.min(n + PAGE, flattened.length))
          } else if (hasNextPage && !isFetchingNextPage) {
            fetchNextPage?.()
          }
        }
      },
      { rootMargin: '800px' },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [rendered.length, flattened.length, hasNextPage, isFetchingNextPage, fetchNextPage])

  // Backward fetch (maxPages dropped the top): when the top sentinel
  // scrolls into view, anchor the viewport then fetch older pages.
  useEffect(() => {
    if (!hasPreviousPage) return
    const el = topSentinelRef.current
    if (!el || typeof IntersectionObserver === 'undefined') return
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          if (hasPreviousPage && !isFetchingPreviousPage) {
            anchorRef.current = el.getBoundingClientRect().top
            fetchPreviousPage?.()
          }
        }
      },
      { rootMargin: '800px' },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [hasPreviousPage, isFetchingPreviousPage, fetchPreviousPage, flattened.length])

  // After prepended pages commit, shift the scroller by the anchor delta
  // and widen the render window by the prepended count so the viewport
  // keeps showing the same rows instead of jumping.
  useLayoutEffect(() => {
    const grown = flattened.length - prevLenRef.current
    prevLenRef.current = flattened.length
    const anchor = anchorRef.current
    anchorRef.current = null
    if (anchor === null || grown <= 0) return
    if (grown >= flattened.length) return // fresh filter, not a prepend
    setRenderLimit((n) => n + grown)
    const el = topSentinelRef.current
    const scroller = document.getElementById('main')
    if (!el || !scroller) return
    const dx = el.getBoundingClientRect().top - anchor
    if (dx !== 0) scroller.scrollTop += dx
  }, [flattened])

  const sortColumn = (key: SortKey) => {
    if (pp) {
      if (pp.sort === key) pp.onSortChange(key, pp.dir === 'asc' ? 'desc' : 'asc')
      else pp.onSortChange(key, 'asc')
      return
    }
    if (legacySort === key) setLegacyDir(legacyDir === 'asc' ? 'desc' : 'asc')
    else {
      setLegacySort(key)
      setLegacyDir('asc')
    }
  }
  const sortValue = pp ? pp.sort : legacySort
  const dirValue = pp ? pp.dir : legacyDir
  const total = pp ? pp.total : flattened.length

  const onPlayRow = (song: Song) => {
    if (pp) {
      const getFiles = pp.getFiles
      void (async () => {
        try {
          await playSongByFiles(song, await getFiles(), source)
        } catch {
          // playSongByFiles reconverges via refresh; nothing to show here.
        }
      })()
    } else {
      void playSong(song, legacyVisible, source)
    }
  }

  return (
    <>
      <div className="sticky top-0 w-full z-10 backdrop-blur-2xl bg-zinc-900/20 rounded-2xl border-b border-white/5 px-6 sm:px-8 py-4 mt-4 flex justify-end items-center max-[700px]:justify-stretch max-[700px]:px-3">
        <div className="max-[700px]:w-full max-[700px]:[&>div]:w-full max-[700px]:[&_input]:w-full">
          <SearchInput value={searchValue} onChange={setSearchValue} placeholder={searchPlaceholder} id={searchId} />
        </div>
      </div>

      <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pb-10 pt-6 max-[700px]:px-2">
        <SortableHeader columns={COLUMNS} sort={sortValue} dir={dirValue} onSort={sortColumn} gridClass={SONG_ROW_GRID} />
        {/* Backward sentinel (maxPages): older pages were dropped — scroll
        up here to refetch them. */}
        {hasPreviousPage && (
          <div ref={topSentinelRef} className="py-3 text-center text-xs text-zinc-600">
            {isFetchingPreviousPage ? 'Loading older…' : 'Scroll up for older songs…'}
          </div>
        )}
        {/* Glass backing so rows stay readable over custom backgrounds */}
        <div className="flex flex-col p-2 sm:p-3 rounded-2xl bg-black/20 backdrop-blur-2xl border border-white/5 shadow-[0_8px_32px_rgba(0,0,0,0.35)] divide-y divide-white/[0.06]">
          {isLoading && <LoadingState label={loadingLabel} />}
          {!isLoading && flattened.length === 0 && <EmptyState title={emptyTitle} hint={emptyHint} />}
          {rendered.map((song) => (
            <div
              key={song.file}
              onContextMenu={(e) => {
                e.preventDefault()
                openMenu(song.file, e.clientX, e.clientY)
              }}
            >
              <SongRow
                song={song}
                active={currentFile === song.file}
                playing={isPlaying && currentFile === song.file}
                onPlay={() => onPlayRow(song)}
                onArtistClick={(artist) => navigate(`/artists/${encodeURIComponent(artist)}`)}
                onAlbumClick={(album) => navigate(`/albums/${encodeURIComponent(album)}`)}
                actions={
                  <>
                    {onRemoveFromPlaylist && (
                      <RemoveFromPlaylistButton onRemove={() => onRemoveFromPlaylist(song)} />
                    )}
                    <SongMenu
                      ref={(h) => {
                        menuRefs.current.set(song.file, h)
                      }}
                      song={song}
                      sourceType={source.type}
                      sourceId={source.id}
                    />
                  </>
                }
              />
            </div>
          ))}
          {/* Infinite-window sentinel: grows renderLimit on scroll, then
          fetches the next server page in paged mode. */}
          {!isLoading && (rendered.length < flattened.length || hasNextPage) && (
            <div ref={sentinelRef} className="py-4 text-center text-xs text-zinc-600">
              {isFetchingNextPage ? 'Loading more…' : `Showing ${rendered.length} of ${total} — scroll for more…`}
            </div>
          )}
        </div>
      </div>
    </>
  )
}
