import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable, { resolveSong, type SortKey } from '../components/LibraryTable'
import { ControlButton, PlayCircleButton } from '../components/ui/buttons'
import ViewHeader from '../components/ui/ViewHeader'
import SearchInput, { useDebouncedValue } from '../components/ui/SearchInput'
import { fuzzyMatch } from '../utils/fuzzy'
import { EmptyState, LoadingState } from '../components/ui/states'
import { SONGS_PAGE_SIZE } from './Songs'

function shuffleList(list: string[]): string[] {
  const out = [...list]
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[out[i], out[j]] = [out[j], out[i]]
  }
  return out
}

// Typo-tolerant client filter for the artists/albums name lists (same
// fuzzy match as the song tables, debounced like every search box).
function useNameFilter(names: readonly string[] | undefined) {
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const filtering = debounced.trim().length > 0
  const visible = useMemo(
    () => (names ?? []).filter((n) => !filtering || fuzzyMatch(n, debounced)),
    [names, debounced, filtering],
  )
  return { search, setSearch, visible, filtering }
}

// Detail-page hero artwork (iTunes 600x600 via the API, exact-match
// preference server-side): large squared tile, music-note fallback while
// loading or when iTunes has nothing.
function HeroArt({ url, alt }: { url: string | null | undefined; alt: string }) {
  if (!url) {
    return (
      <div className="w-28 h-28 sm:w-36 sm:h-36 flex-shrink-0 flex items-center justify-center bg-white/5 border border-white/10 rounded-2xl">
        <svg viewBox="0 0 24 24" fill="currentColor" className="w-12 h-12 text-zinc-600">
          <path d="M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zM9 10l12-3" />
        </svg>
      </div>
    )
  }
  return (
    <img
      src={url}
      alt={alt}
      loading="lazy"
      className="w-28 h-28 sm:w-36 sm:h-36 flex-shrink-0 object-cover border border-white/10 rounded-2xl"
    />
  )
}

// RAM windowing for the plain name lists (artists/albums can number in
// the thousands — mounting one <Link> each is cheap per row but adds up).
function useNameWindow(resetKey: string) {
  const PAGE = 200
  const [limit, setLimit] = useState(PAGE)
  const sentinelRef = useRef<HTMLDivElement | null>(null)
  useEffect(() => {
    setLimit(PAGE)
  }, [resetKey])
  return { limit, PAGE, sentinelRef, setLimit }
}

function NameSentinel({
  sentinelRef,
  rendered,
  total,
  onGrow,
}: {
  sentinelRef: React.RefObject<HTMLDivElement | null>
  rendered: number
  total: number
  onGrow: () => void
}) {
  useEffect(() => {
    if (rendered >= total) return
    const el = sentinelRef.current
    if (!el || typeof IntersectionObserver === 'undefined') return
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) onGrow()
        }
      },
      { rootMargin: '800px' },
    )
    io.observe(el)
    return () => io.disconnect()
  }, [rendered, total, onGrow, sentinelRef])
  if (rendered >= total) return null
  return (
    <div ref={sentinelRef} className="py-4 text-center text-xs text-zinc-600">
      Showing {rendered} of {total} — scroll for more…
    </div>
  )
}

export function Artists() {
  const { data: artists, isLoading } = useQuery({ queryKey: ['artists'], queryFn: api.artists })
  const { search, setSearch, visible, filtering } = useNameFilter(artists)
  const win = useNameWindow(search)
  const rendered = visible.slice(0, win.limit)
  const grow = useCallback(
    () => win.setLimit((n: number) => Math.min(n + win.PAGE, visible.length)),
    [visible.length, win],
  )
  return (
    <div className="max-w-5xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24 flex flex-col gap-6">
      <ViewHeader
        icon="users"
        title="Artists"
        subtitle={`${artists?.length ?? 0} artists`}
        actions={
          <SearchInput value={search} onChange={setSearch} placeholder="Search artists..." id="artists_txt_search" />
        }
      />
      {isLoading ? (
        <LoadingState label="Loading artists…" />
      ) : (artists ?? []).length === 0 ? (
        <EmptyState title="No artists yet" hint="Artists appear once your library has tagged music." />
      ) : filtering && visible.length === 0 ? (
        <EmptyState title="No artists match" hint="Try different spelling." />
      ) : (
        <div className="flex flex-col p-2 sm:p-3 rounded-2xl bg-black/30 backdrop-blur-2xl border border-white/5 shadow-[0_8px_32px_rgba(0,0,0,0.35)] divide-y divide-white/[0.06]">
          {/* Same windows-glass backing as the song tables (LibraryTable). */}
          {rendered.map((name) => (
            <Link
              key={name}
              to={`/artists/${encodeURIComponent(name)}`}
              className="p-2.5 px-3 sm:p-3 sm:px-4 rounded-xl hover:bg-white/5 transition-all text-sm font-medium text-zinc-100 hover:text-orange-400 truncate"
            >
              {name}
            </Link>
          ))}
          <NameSentinel sentinelRef={win.sentinelRef} rendered={rendered.length} total={visible.length} onGrow={grow} />
        </div>
      )}
    </div>
  )
}

export function ArtistDetail() {
  const { name } = useParams()
  // useParams already decodes percent-encoding; a second
  // decodeURIComponent would corrupt names and throw on literals like '%'.
  const artist = name ?? ''
  const playSongByFiles = usePlayer((s) => s.playSongByFiles)
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const [sort, setSort] = useState<SortKey | null>(null)
  const [dir, setDir] = useState<'asc' | 'desc'>('asc')
  const { data: total } = useQuery({
    queryKey: ['artist-songs', 'count', artist, debounced],
    queryFn: () => api.artistSongCount(artist, debounced || undefined),
    enabled: artist.length > 0,
  })
  const {
    data,
    isLoading,
    isFetchingNextPage,
    isFetchingPreviousPage,
    hasNextPage,
    hasPreviousPage,
    fetchNextPage,
    fetchPreviousPage,
  } = useInfiniteQuery({
    queryKey: ['artist-songs', 'inf', artist, debounced, sort, dir],
    queryFn: ({ pageParam }) =>
      api.artistSongs(artist, {
        search: debounced || undefined,
        sort: sort ?? undefined,
        dir: sort ? dir : undefined,
        page: pageParam,
        pageSize: SONGS_PAGE_SIZE,
      }),
    enabled: artist.length > 0,
    initialPageParam: 1,
    maxPages: 3,
    getNextPageParam: (lastPage, _allPages, lastParam) =>
      lastPage.length < SONGS_PAGE_SIZE ? undefined : lastParam + 1,
    getPreviousPageParam: (_firstPage, _allPages, firstParam) =>
      firstParam > 1 ? firstParam - 1 : undefined,
  })
  const pages = data?.pages
  const getFiles = () => api.artistFiles(artist, debounced || undefined, sort ?? undefined, sort ? dir : undefined)
  const playAll = (shuffled: boolean) => {
    void (async () => {
      const files = await getFiles()
      if (files.length === 0) return
      const ordered = shuffled ? shuffleList(files) : files
      const first = ordered[0]
      const song = await resolveSong(pages, first)
      await playSongByFiles(song, ordered, { type: 'artist', id: artist }).catch(() => {})
    })()
  }
  const { data: art } = useQuery({
    queryKey: ['artist-image', artist],
    queryFn: () => api.artistImage(artist),
    enabled: artist.length > 0,
    staleTime: 1000 * 60 * 60,
  })
  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6 flex items-center gap-4 max-[700px]:flex-wrap max-[700px]:gap-y-3">
        <HeroArt url={art?.url} alt={artist} />
        <ViewHeader icon="users" compact title={artist} subtitle={`${total ?? 0} songs`} />
        {(total ?? 0) > 0 && (
          <div className="ml-auto flex items-center gap-2 flex-shrink-0">
            <ControlButton icon="shuffle" title="Shuffle play" onClick={() => playAll(true)} />
            <PlayCircleButton title="Play all" onClick={() => playAll(false)} />
          </div>
        )}
      </div>
      <LibraryTable
        pages={pages}
        total={total ?? 0}
        isLoading={isLoading}
        isFetchingNextPage={isFetchingNextPage}
        isFetchingPreviousPage={isFetchingPreviousPage}
        hasNextPage={hasNextPage ?? false}
        hasPreviousPage={hasPreviousPage ?? false}
        fetchNextPage={() => void fetchNextPage()}
        fetchPreviousPage={() => void fetchPreviousPage()}
        search={search}
        onSearchChange={setSearch}
        sort={sort}
        dir={dir}
        onSortChange={(s, d) => {
          setSort(s)
          setDir(d)
        }}
        getFiles={getFiles}
        loadingLabel="Loading artist…"
        emptyTitle="No songs by this artist"
        source={{ type: 'artist', id: artist }}
        searchPlaceholder="Search artist..."
      />
    </div>
  )
}

export function Albums() {
  const { data: albums, isLoading } = useQuery({ queryKey: ['albums'], queryFn: api.albums })
  const { search, setSearch, visible, filtering } = useNameFilter(albums)
  const win = useNameWindow(search)
  const rendered = visible.slice(0, win.limit)
  const grow = useCallback(
    () => win.setLimit((n: number) => Math.min(n + win.PAGE, visible.length)),
    [visible.length, win],
  )
  return (
    <div className="max-w-5xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24 flex flex-col gap-6">
      <ViewHeader
        icon="musicNote"
        title="Albums"
        subtitle={`${albums?.length ?? 0} albums`}
        actions={
          <SearchInput value={search} onChange={setSearch} placeholder="Search albums..." id="albums_txt_search" />
        }
      />
      {isLoading ? (
        <LoadingState label="Loading albums…" />
      ) : (albums ?? []).length === 0 ? (
        <EmptyState title="No albums yet" hint="Albums appear once your library has tagged music." />
      ) : filtering && visible.length === 0 ? (
        <EmptyState title="No albums match" hint="Try different spelling." />
      ) : (
        <div className="flex flex-col p-2 sm:p-3 rounded-2xl bg-black/30 backdrop-blur-2xl border border-white/5 shadow-[0_8px_32px_rgba(0,0,0,0.35)] divide-y divide-white/[0.06]">
          {/* Same windows-glass backing as the song tables (LibraryTable). */}
          {rendered.map((title) => (
            <Link
              key={title}
              to={`/albums/${encodeURIComponent(title)}`}
              className="p-2.5 px-3 sm:p-3 sm:px-4 rounded-xl hover:bg-white/5 transition-all text-sm font-medium text-zinc-100 hover:text-orange-400 truncate"
            >
              {title}
            </Link>
          ))}
          <NameSentinel sentinelRef={win.sentinelRef} rendered={rendered.length} total={visible.length} onGrow={grow} />
        </div>
      )}
    </div>
  )
}

export function AlbumDetail() {
  const { title } = useParams()
  // useParams already decodes percent-encoding (see ArtistDetail).
  const album = title ?? ''
  const playSongByFiles = usePlayer((s) => s.playSongByFiles)
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const [sort, setSort] = useState<SortKey | null>(null)
  const [dir, setDir] = useState<'asc' | 'desc'>('asc')
  const { data: total } = useQuery({
    queryKey: ['album-songs', 'count', album, debounced],
    queryFn: () => api.albumSongCount(album, debounced || undefined),
    enabled: album.length > 0,
  })
  const {
    data,
    isLoading,
    isFetchingNextPage,
    isFetchingPreviousPage,
    hasNextPage,
    hasPreviousPage,
    fetchNextPage,
    fetchPreviousPage,
  } = useInfiniteQuery({
    queryKey: ['album-songs', 'inf', album, debounced, sort, dir],
    queryFn: ({ pageParam }) =>
      api.albumSongs(album, {
        search: debounced || undefined,
        sort: sort ?? undefined,
        dir: sort ? dir : undefined,
        page: pageParam,
        pageSize: SONGS_PAGE_SIZE,
      }),
    enabled: album.length > 0,
    initialPageParam: 1,
    maxPages: 3,
    getNextPageParam: (lastPage, _allPages, lastParam) =>
      lastPage.length < SONGS_PAGE_SIZE ? undefined : lastParam + 1,
    getPreviousPageParam: (_firstPage, _allPages, firstParam) =>
      firstParam > 1 ? firstParam - 1 : undefined,
  })
  const pages = data?.pages
  const getFiles = () => api.albumFiles(album, debounced || undefined, sort ?? undefined, sort ? dir : undefined)
  const playAll = (shuffled: boolean) => {
    void (async () => {
      const files = await getFiles()
      if (files.length === 0) return
      const ordered = shuffled ? shuffleList(files) : files
      const first = ordered[0]
      const song = await resolveSong(pages, first)
      await playSongByFiles(song, ordered, { type: 'album', id: album }).catch(() => {})
    })()
  }
  // Disambiguate same-name albums when every loaded track agrees on the
  // artist (first page almost always settles it: the view is Artist+Title
  // ordered, so one artist's tracks are contiguous).
  const artists = [...new Set((pages ?? []).flat().map((s) => s.artist).filter((a) => a && a !== 'Unknown'))]
  const albumArtist = artists.length === 1 ? artists[0] : undefined
  const { data: art } = useQuery({
    queryKey: ['album-image', album, albumArtist ?? ''],
    queryFn: () => api.albumImage(album, albumArtist),
    enabled: album.length > 0,
    staleTime: 1000 * 60 * 60,
  })
  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6 flex items-center gap-4 max-[700px]:flex-wrap max-[700px]:gap-y-3">
        <HeroArt url={art?.url} alt={album} />
        <ViewHeader icon="musicNote" compact title={album} subtitle={`${total ?? 0} songs`} />
        {(total ?? 0) > 0 && (
          <div className="ml-auto flex items-center gap-2 flex-shrink-0">
            <ControlButton icon="shuffle" title="Shuffle play" onClick={() => playAll(true)} />
            <PlayCircleButton title="Play all" onClick={() => playAll(false)} />
          </div>
        )}
      </div>
      <LibraryTable
        pages={pages}
        total={total ?? 0}
        isLoading={isLoading}
        isFetchingNextPage={isFetchingNextPage}
        isFetchingPreviousPage={isFetchingPreviousPage}
        hasNextPage={hasNextPage ?? false}
        hasPreviousPage={hasPreviousPage ?? false}
        fetchNextPage={() => void fetchNextPage()}
        fetchPreviousPage={() => void fetchPreviousPage()}
        search={search}
        onSearchChange={setSearch}
        sort={sort}
        dir={dir}
        onSortChange={(s, d) => {
          setSort(s)
          setDir(d)
        }}
        getFiles={getFiles}
        loadingLabel="Loading album…"
        emptyTitle="No songs on this album"
        source={{ type: 'album', id: album }}
        searchPlaceholder="Search album..."
      />
    </div>
  )
}
