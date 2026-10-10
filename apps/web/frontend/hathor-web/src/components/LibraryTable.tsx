import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { QueueSource, Song } from '../api/client'
import { usePlayer } from '../store/player'
import SearchInput, { useDebouncedValue } from './ui/SearchInput'
import { fuzzyFields } from '../utils/fuzzy'
import Icon from './ui/icons'
import SongMenu, { type SongMenuHandle } from './ui/SongMenu'
import SongRow, { SONG_ROW_GRID } from './ui/SongRow'
import SortableHeader, { type SortColumn } from './ui/SortableHeader'
import { EmptyState, LoadingState } from './ui/states'

type SortKey = 'Title' | 'Album' | 'Duration' | 'DateDownload'

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

// Shared searchable/sortable song table (Songs, Playlist detail,
// Artist/Album detail). Rows play with the visible list as queue context.
export default function LibraryTable({
  songs,
  isLoading,
  loadingLabel = 'Loading…',
  emptyTitle = 'No songs yet',
  emptyHint,
  source,
  searchPlaceholder = 'Search...',
  searchId,
  onRemoveFromPlaylist,
}: {
  songs: Song[] | undefined
  isLoading: boolean
  loadingLabel?: string
  emptyTitle?: string
  emptyHint?: string
  source: QueueSource
  searchPlaceholder?: string
  searchId?: string
  onRemoveFromPlaylist?: (song: Song) => void
}) {
  const playSong = usePlayer((s) => s.playSong)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const isPlaying = usePlayer((s) => s.isPlaying)
  const navigate = useNavigate()
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const [sort, setSort] = useState<SortKey | null>(null)
  const [dir, setDir] = useState<'asc' | 'desc'>('asc')
  const menuRefs = useRef(new Map<string, SongMenuHandle | null>())
  const openMenu = (file: string, x: number, y: number) =>
    menuRefs.current.get(file)?.openAt(x, y)

  // Typo-tolerant client filter (mirrors the API ?search= fuzzy match),
  // memoized: the fuzzy pass over thousands of rows runs only when its
  // inputs change, not on every parent/store render.
  const visible = useMemo(() => {
    const list = (songs ?? []).filter(
      (s) => debounced.trim().length === 0 || fuzzyFields([s.title, s.artist, s.album], debounced),
    )
    if (!sort) return list
    const d = dir === 'asc' ? 1 : -1
    return [...list].sort((a, b) => {
      if (sort === 'Duration') return (a.duration - b.duration) * d
      if (sort === 'DateDownload') {
        // ISO timestamps sort lexicographically; missing dates go last.
        const av = a.dateDownload ?? ''
        const bv = b.dateDownload ?? ''
        if (!av) return 1
        if (!bv) return -1
        return av < bv ? -d : av > bv ? d : 0
      }
      const av = (a[sort === 'Title' ? 'title' : 'album'] ?? '').toLowerCase()
      const bv = (b[sort === 'Title' ? 'title' : 'album'] ?? '').toLowerCase()
      return av < bv ? -d : av > bv ? d : 0
    })
  }, [songs, debounced, sort, dir])

  // Drop menu handles for rows that no longer exist (unbounded Map growth
  // across searches otherwise).
  useEffect(() => {
    const alive = new Set((songs ?? []).map((s) => s.file))
    for (const key of menuRefs.current.keys()) {
      if (!alive.has(key)) menuRefs.current.delete(key)
    }
  }, [songs])

const sortColumn = (key: SortKey) => {
    if (sort === key) setDir(dir === 'asc' ? 'desc' : 'asc')
    else {
      setSort(key)
      setDir('asc')
    }
  }

  return (
    <>
      <div className="sticky top-0 w-full z-10 backdrop-blur-2xl bg-zinc-900/20 rounded-2xl border-b border-white/5 px-6 sm:px-8 py-4 mt-4 flex justify-end items-center max-[700px]:justify-stretch max-[700px]:px-3">
        <div className="max-[700px]:w-full max-[700px]:[&>div]:w-full max-[700px]:[&_input]:w-full">
          <SearchInput value={search} onChange={setSearch} placeholder={searchPlaceholder} id={searchId} />
        </div>
      </div>

      <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pb-10 pt-6 max-[700px]:px-2">
        <SortableHeader columns={COLUMNS} sort={sort} dir={dir} onSort={sortColumn} gridClass={SONG_ROW_GRID} />
        {/* Glass backing so rows stay readable over custom backgrounds */}
        <div className="flex flex-col p-2 sm:p-3 rounded-2xl bg-black/20 backdrop-blur-2xl border border-white/5 shadow-[0_8px_32px_rgba(0,0,0,0.35)] divide-y divide-white/[0.06]">
          {isLoading && <LoadingState label={loadingLabel} />}
          {!isLoading && visible.length === 0 && <EmptyState title={emptyTitle} hint={emptyHint} />}
          {visible.map((song) => (
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
                onPlay={() => void playSong(song, visible, source)}
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
        </div>
      </div>
    </>
  )
}
