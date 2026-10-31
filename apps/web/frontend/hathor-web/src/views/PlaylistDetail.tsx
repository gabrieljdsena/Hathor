import { useState } from 'react'
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useParams } from 'react-router-dom'
import { api } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable, { resolveSong, type SortKey } from '../components/LibraryTable'
import Modal from '../components/ui/Modal'
import { GhostButton, PrimaryButton } from '../components/ui/fields'
import { PlayCircleButton } from '../components/ui/buttons'
import PlaylistFormModal from '../components/ui/PlaylistFormModal'
import { EmptyState, LoadingState } from '../components/ui/states'
import { useDebouncedValue } from '../components/ui/SearchInput'
import { PlaylistCover } from './Playlists'
import { SONGS_PAGE_SIZE } from './Songs'

// Playlist detail: header (cover + counts + play/edit/delete) + song table.
// Server-paged infinite scroll (link DateAdded order); the queue resolves
// via /playlists/{id}/files so the full list is never materialized.
export default function PlaylistDetail() {
  const { id } = useParams()
  const playlistId = Number(id)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const playSongByFiles = usePlayer((s) => s.playSongByFiles)
  const [editOpen, setEditOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const [sort, setSort] = useState<SortKey | null>(null)
  const [dir, setDir] = useState<'asc' | 'desc'>('asc')

  const { data: playlists } = useQuery({ queryKey: ['playlists'], queryFn: api.playlists })
  const playlist = playlists?.find((p) => p.id === playlistId)
  const { data: total } = useQuery({
    queryKey: ['playlist-songs', 'count', playlistId, debounced],
    queryFn: () => api.playlistSongsCount(playlistId, debounced || undefined),
    enabled: Number.isFinite(playlistId),
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
    queryKey: ['playlist-songs', 'inf', playlistId, debounced, sort, dir],
    queryFn: ({ pageParam }) =>
      api.playlistSongs(playlistId, {
        search: debounced || undefined,
        sort: sort ?? undefined,
        dir: sort ? dir : undefined,
        page: pageParam,
        pageSize: SONGS_PAGE_SIZE,
      }),
    enabled: Number.isFinite(playlistId),
    initialPageParam: 1,
    // RAM: retain at most 3 pages (see Songs.tsx).
    maxPages: 3,
    getNextPageParam: (lastPage, _allPages, lastParam) =>
      lastPage.length < SONGS_PAGE_SIZE ? undefined : lastParam + 1,
    getPreviousPageParam: (_firstPage, _allPages, firstParam) =>
      firstParam > 1 ? firstParam - 1 : undefined,
  })
  const pages = data?.pages
  const getFiles = () => api.playlistFiles(playlistId, debounced || undefined, sort ?? undefined, sort ? dir : undefined)

  const refreshSongs = () => {
    // Prefix invalidations cover the inf/count pages for every filter.
    void queryClient.invalidateQueries({ queryKey: ['playlist-songs'] })
    void queryClient.invalidateQueries({ queryKey: ['playlists'] })
  }

  const playAll = () => {
    void (async () => {
      const files = await getFiles()
      const first = files[0]
      if (!first) return
      const song = await resolveSong(pages, first)
      await playSongByFiles(song, files, {
        type: 'playlist',
        id: String(playlistId),
      }).catch(() => {})
    })()
  }

  if (!Number.isFinite(playlistId)) return <EmptyState title="Playlist not found" />

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        {playlists === undefined ? (
          <LoadingState label="Loading playlist…" />
        ) : !playlist ? (
          <EmptyState title="Playlist not found" hint="It may have been deleted." />
        ) : (
          <div className="flex items-center gap-4 max-[700px]:flex-wrap max-[700px]:gap-y-3">
            <PlaylistCover src={playlist.thumbnail} title={playlist.title} />
            <div className="min-w-0 flex-1">
              <h1 className="text-2xl font-bold text-zinc-100 truncate">{playlist.title}</h1>
              {playlist.description && <p className="text-sm text-zinc-500 mt-1 truncate">{playlist.description}</p>}
              <p className="text-xs text-zinc-500 tracking-wide mt-0.5">
                {total ?? 0} song{(total ?? 0) === 1 ? '' : 's'}
              </p>
            </div>
            <div className="ml-auto flex items-center gap-2">
              <GhostButton onClick={() => setEditOpen(true)}>Edit</GhostButton>
              <GhostButton
                onClick={() => setDeleteOpen(true)}
                className="hover:bg-red-500/20 hover:text-red-400 hover:border-red-500/50"
              >
                Delete
              </GhostButton>
              <PlayCircleButton title="Play playlist" onClick={playAll} />
            </div>
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
        loadingLabel="Loading playlist songs…"
        emptyTitle="Nothing here yet"
        emptyHint="Add songs from any song menu."
        source={{ type: 'playlist', id: String(playlistId) }}
        searchPlaceholder="Search playlist..."
        onRemoveFromPlaylist={(song) => {
          void api.removeSongFromPlaylist(playlistId, song.file).then(() => refreshSongs())
        }}
      />

      {editOpen && playlist && (
        <PlaylistFormModal
          playlist={playlist}
          onClose={() => setEditOpen(false)}
          onSaved={() => {
            setEditOpen(false)
            void queryClient.invalidateQueries({ queryKey: ['playlists'] })
          }}
        />
      )}

      <Modal open={deleteOpen} onClose={() => setDeleteOpen(false)} title="Delete playlist?">
        <p className="text-sm text-zinc-400">
          <span className="text-zinc-200 font-medium">{playlist?.title}</span> will be deleted. Songs stay in your
          library.
        </p>
        <div className="flex justify-end gap-3 pt-4 mt-4 border-t border-white/10">
          <GhostButton onClick={() => setDeleteOpen(false)}>Cancel</GhostButton>
          <PrimaryButton
            onClick={() =>
              void api
                .deletePlaylist(playlistId)
                .then(() => {
                  void queryClient.invalidateQueries({ queryKey: ['playlists'] })
                  navigate('/playlists')
                })
                .catch(() => setDeleteOpen(false))
            }
          >
            Yes, delete it
          </PrimaryButton>
        </div>
      </Modal>
    </div>
  )
}
