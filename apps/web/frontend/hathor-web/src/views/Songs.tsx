import { useState } from 'react'
import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { api, type Song } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable, { resolveSong, type SortKey } from '../components/LibraryTable'
import { PlayCircleButton } from '../components/ui/buttons'
import ViewHeader from '../components/ui/ViewHeader'
import { useDebouncedValue } from '../components/ui/SearchInput'

// Page size for infinite scroll: 200 slim DTOs (~40KB) per fetch.
export const SONGS_PAGE_SIZE = 200

// Library table cloned from Music Player/ui/views/all_songs.html.
// Server-paged infinite scroll: search/sort/page live in the query key,
// the queue resolves via the lightweight /songs/files endpoint so the
// full Song[] list is never materialized client-side.
export default function Songs() {
  const playSongByFiles = usePlayer((s) => s.playSongByFiles)
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const [sort, setSort] = useState<SortKey | null>(null)
  const [dir, setDir] = useState<'asc' | 'desc'>('asc')

  const { data: total } = useQuery({
    queryKey: ['songs', 'count', debounced],
    queryFn: () => api.songCount(debounced || undefined),
  })
  const {
    data,
    isLoading,
    isError,
    isFetchingNextPage,
    isFetchingPreviousPage,
    hasNextPage,
    hasPreviousPage,
    fetchNextPage,
    fetchPreviousPage,
  } = useInfiniteQuery({
    queryKey: ['songs', 'inf', debounced, sort, dir],
    queryFn: ({ pageParam }) =>
      api.songs({
        search: debounced || undefined,
        sort: sort ?? undefined,
        dir: sort ? dir : undefined,
        page: pageParam,
        pageSize: SONGS_PAGE_SIZE,
      }),
    initialPageParam: 1,
    // RAM: retain at most 3 pages (~600 slim DTOs). Page numbers come
    // from params (not array length — maxPages drops old entries).
    // Scrolling far in either direction refetches instead of holding the
    // whole scrolled library in memory.
    maxPages: 3,
    getNextPageParam: (lastPage, _allPages, lastParam) =>
      lastPage.length < SONGS_PAGE_SIZE ? undefined : lastParam + 1,
    getPreviousPageParam: (_firstPage, _allPages, firstParam) =>
      firstParam > 1 ? firstParam - 1 : undefined,
  })
  const pages = data?.pages

  const getFiles = () =>
    api.songsFiles({
      search: debounced || undefined,
      sort: sort ?? undefined,
      dir: sort ? dir : undefined,
    })

  const playAll = async () => {
    const files = await getFiles()
    const first = files[0]
    if (!first) return
    const song: Song = await resolveSong(pages, first)
    await playSongByFiles(song, files, { type: 'all_songs', id: null }).catch(() => {})
  }

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        <ViewHeader
          icon="musicNote"
          compact
          title="All Songs"
          subtitle={isLoading ? 'Loading…' : `${total ?? 0} songs`}
          actions={<PlayCircleButton title="Play all" onClick={() => void playAll()} />}
        />
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
        loadingLabel="Loading your library…"
        emptyTitle={isError ? 'Could not load songs' : 'No songs yet'}
        emptyHint={isError ? 'Check your connection and try again.' : 'Download some music to build your library.'}
        source={{ type: 'all_songs', id: null }}
        searchId="all_txt_search"
      />
    </div>
  )
}
