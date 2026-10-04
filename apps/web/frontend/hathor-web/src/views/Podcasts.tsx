import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type Song } from '../api/client'
import { usePlayer } from '../store/player'
import CoverArt, { evictCoverCache } from '../components/ui/CoverArt'
import EditSongModal from '../components/ui/EditSongModal'
import Icon from '../components/ui/icons'
import { claimMenu, releaseMenu, subscribeMenu } from '../components/ui/menuBus'
import Modal from '../components/ui/Modal'
import { AssignTagsModal, TagManagerModal, TagPills } from '../components/ui/PodcastTags'
import SearchInput, { useDebouncedValue } from '../components/ui/SearchInput'
import { fuzzyFields } from '../utils/fuzzy'
import { EmptyState, LoadingState } from '../components/ui/states'
import ViewHeader from '../components/ui/ViewHeader'
import { GhostButton, PrimaryButton } from '../components/ui/fields'
import { PlayCircleButton } from '../components/ui/buttons'

// Podcasts view cloned from ui/views/podcasts.html: header + search +
// tag chips + episode rows + bento menu + tag modals.
export default function Podcasts() {
  const queryClient = useQueryClient()
  const playSong = usePlayer((s) => s.playSong)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const [search, setSearch] = useState('')
  const debounced = useDebouncedValue(search)
  const [activeTag, setActiveTag] = useState<number | null>(null)
  const [menuFile, setMenuFile] = useState<string | null>(null)
  const [tagsOpen, setTagsOpen] = useState(false)
  const [assignFile, setAssignFile] = useState<Song | null>(null)
  const [editSong, setEditSong] = useState<Song | null>(null)
  const [deleteSong, setDeleteSong] = useState<Song | null>(null)
  const menuFileRef = useRef<string | null>(null)
  menuFileRef.current = menuFile

  const openEpisodeMenu = (file: string) => {
    // Sync the ref before claiming: claimMenu notifies synchronously and
    // the subscriber must see the menu as ours, not close it instantly.
    menuFileRef.current = file
    setMenuFile(file)
    claimMenu(`podcast:${file}`)
  }
  const closeEpisodeMenu = () => {
    if (menuFileRef.current) releaseMenu(`podcast:${menuFileRef.current}`)
    menuFileRef.current = null
    setMenuFile(null)
  }

  // Another menu claimed the bus: close this one (single open menu globally).
  useEffect(
    () =>
      subscribeMenu((id) => {
        const mine = menuFileRef.current ? `podcast:${menuFileRef.current}` : null
        if (id !== null && id !== mine) setMenuFile(null)
      }),
    [],
  )

  const { data: episodes, isLoading } = useQuery({ queryKey: ['podcasts'], queryFn: api.podcasts })
  const { data: tags } = useQuery({ queryKey: ['podcast-tags'], queryFn: api.podcastTags })
  const { data: tagMap } = useQuery({ queryKey: ['podcast-tag-map'], queryFn: api.podcastTagMap })

  // A deleted tag must not keep filtering the list (its episodes would
  // vanish until manual refresh): fall back to All when it disappears.
  useEffect(() => {
    if (activeTag !== null && tags && !tags.some((t) => t.id === activeTag)) setActiveTag(null)
  }, [activeTag, tags])

  const refresh = () => {
    // Episode metadata also appears under song-ish keys (history, recents):
    // invalidate everything so edits show live on any view.
    void queryClient.invalidateQueries()
  }

  // Typo-tolerant client filter (same fuzzy rules as the song search),
  // memoized: tag lookups + fuzzy pass rerun only when inputs change.
  const tagById = useMemo(() => new Map((tags ?? []).map((t) => [t.id, t.name])), [tags])
  const visible = useMemo(
    () =>
      (episodes ?? []).filter((s) => {
        const qOk = debounced.trim().length === 0 || fuzzyFields([s.title, s.artist], debounced)
        const tOk = activeTag === null || (tagMap?.[s.file] ?? []).includes(activeTag)
        return qOk && tOk
      }),
    [episodes, debounced, activeTag, tagMap],
  )

  const playEpisode = (song: Song) => {
    const ep = { ...song, isPodcast: true }
    void playSong(ep, visible.map((s) => ({ ...s, isPodcast: true })), {
      type: 'podcast',
      id: song.file,
    })
  }

  const playAll = () => {
    const first = visible[0]
    if (first) playEpisode(first)
  }

  const tagNames = (file: string) => {
    const ids = tagMap?.[file] ?? []
    return ids.map((id) => tagById.get(id)).filter((n): n is string => !!n)
  }

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6 flex items-center gap-4">
        <ViewHeader
          icon="mic"
          compact
          title="Podcasts"
          subtitle={isLoading ? 'Loading…' : `${episodes?.length ?? 0} episodes`}
        />
        <div className="ml-auto flex items-center gap-2">
          <button
            onClick={() => setTagsOpen(true)}
            title="Manage tags"
            className="flex items-center justify-center w-11 h-11 rounded-full bg-white/5 border border-white/10 text-zinc-300 hover:text-white hover:bg-white/10 transition-all active:scale-95 cursor-pointer"
          >
            <Icon name="tag" className="w-5 h-5" />
          </button>
          <PlayCircleButton title="Play all" onClick={playAll} />
        </div>
      </div>

      <div className="sticky top-0 w-full z-10 backdrop-blur-2xl bg-zinc-900/20 rounded-2xl border-b border-white/5 px-6 sm:px-8 py-4 mt-4 flex justify-end items-center">
        <SearchInput value={search} onChange={setSearch} placeholder="Search episodes..." id="pod_txt_search" />
      </div>

      <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pb-10 pt-6">
        <TagPills tags={tags ?? []} total={episodes?.length ?? 0} activeId={activeTag} onSelect={setActiveTag} />
        {isLoading ? (
          <LoadingState label="Loading your episodes…" />
        ) : visible.length === 0 ? (
          <EmptyState
            title="No episodes yet"
            hint="Download something and mark it as podcast / audio to build this library."
          />
        ) : (
          /* Glass backing so rows stay readable over custom backgrounds */
          <div className="flex flex-col p-2 sm:p-3 rounded-2xl bg-black/20 backdrop-blur-2xl border border-white/5 shadow-[0_8px_32px_rgba(0,0,0,0.35)] divide-y divide-white/[0.06]">
            {visible.map((song) => {
              const names = tagNames(song.file)
              return (
                <div
                  key={song.file}
                  onClick={() => playEpisode(song)}
                  onContextMenu={(e) => {
                    e.preventDefault()
                    openEpisodeMenu(song.file)
                  }}
                  className={`grid grid-cols-[minmax(0,1fr)_auto_auto] gap-4 items-center p-3 px-4 rounded-xl hover:bg-white/5 transition-all duration-300 cursor-pointer group ${
                    currentFile === song.file ? 'bg-orange-500/[0.06] ring-1 ring-orange-500/40' : ''
                  }`}
                >
                  <div className="flex items-center gap-3 overflow-hidden">
                    <CoverArt src={song.coverArt} file={song.file} isPodcast alt={song.title} className="w-12 h-12 rounded-lg flex-shrink-0" />
                    <div className="flex flex-col min-w-0">
                      <div className="text-sm font-medium text-zinc-100 truncate group-hover:text-orange-400 transition-colors">
                        {song.title}
                      </div>
                      <div className="text-xs text-zinc-500 truncate mt-0.5">
                        {[song.artist !== 'Unknown' ? song.artist : null, names.join(', ') || null]
                          .filter(Boolean)
                          .join(' • ')}
                      </div>
                    </div>
                  </div>
                  <div className="text-sm text-zinc-500 font-medium text-right whitespace-nowrap">
                    {song.duration ? formatEpDuration(song.duration) : '—'}
                  </div>
                  <div className="relative">
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        if (menuFile === song.file) closeEpisodeMenu()
                        else openEpisodeMenu(song.file)
                      }}
                      title="More actions"
                      className="p-2 rounded-full text-zinc-600 hover:text-white hover:bg-white/10 transition-all cursor-pointer"
                    >
                      <Icon name="dots" className="w-5 h-5 pointer-events-none" />
                    </button>
                    {menuFile === song.file && (
                      <EpisodeMenu
                        song={song}
                        onClose={closeEpisodeMenu}
                        onPlay={() => playEpisode(song)}
                        onEdit={() => setEditSong({ ...song, isPodcast: true })}
                        onTags={() => setAssignFile({ ...song, isPodcast: true })}
                        onDelete={() => setDeleteSong({ ...song, isPodcast: true })}
                        onGetMetadata={() =>
                          void api
                            .podcastDetails(song.file)
                            .then(() => refresh())
                            .catch(() => {})
                        }
                      />
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>

      {tagsOpen && (
        <TagManagerModal
          onClose={() => {
            setTagsOpen(false)
            refresh()
          }}
        />
      )}
      {assignFile && (
        <AssignTagsModal
          file={assignFile.file}
          title={assignFile.title}
          onClose={() => {
            setAssignFile(null)
            refresh()
          }}
        />
      )}
      {editSong && (
        <EditSongModal
          song={editSong}
          onClose={() => setEditSong(null)}
          onSaved={() => {
            setEditSong(null)
            refresh()
            void usePlayer.getState().refresh().catch(() => {})
          }}
        />
      )}
      <Modal open={deleteSong !== null} onClose={() => setDeleteSong(null)} title="Delete episode?">
        <p className="text-sm text-zinc-400">The file will be permanently removed.</p>
        <div className="flex justify-end gap-3 pt-4 mt-4 border-t border-white/10">
          <GhostButton onClick={() => setDeleteSong(null)}>Cancel</GhostButton>
          <PrimaryButton
            onClick={() => {
              const f = deleteSong
              if (!f) return
              void api
                .deletePodcast(f.file)
                .then(() => {
                  setDeleteSong(null)
                  evictCoverCache(f.file)
                  refresh()
                  void usePlayer.getState().refresh()
                })
                .catch(() => setDeleteSong(null))
            }}
          >
            Yes, delete it
          </PrimaryButton>
        </div>
      </Modal>
    </div>
  )
}

function formatEpDuration(totalSecs: number) {
  const s = Math.floor(totalSecs || 0)
  if (!s) return '—'
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

function EpisodeMenu({
  song,
  onClose,
  onPlay,
  onEdit,
  onTags,
  onDelete,
  onGetMetadata,
}: {
  song: Song
  onClose: () => void
  onPlay: () => void
  onEdit: () => void
  onTags: () => void
  onDelete: () => void
  onGetMetadata: () => void
}) {
  const entry = (
    label: string,
    icon: 'play' | 'next' | 'list' | 'search' | 'gear' | 'tag' | 'x',
    run: () => void,
    danger = false,
  ) => (
    <button
      key={label}
      onClick={(e) => {
        e.stopPropagation()
        onClose()
        run()
      }}
      className={`w-full text-left px-4 py-2 text-sm transition-colors flex items-center gap-2 cursor-pointer ${
        danger ? 'text-red-400 hover:text-red-300' : 'text-zinc-300 hover:bg-white/5 hover:text-white'
      }`}
    >
      <Icon name={icon} className="w-4 h-4 pointer-events-none" />
      {label}
    </button>
  )
  return (
    <div className="absolute right-0 top-full mt-1 z-50 w-48 py-1 bg-zinc-800 border border-white/10 rounded-xl shadow-2xl">
      {entry('Play', 'play', onPlay)}
      {entry('Play Next', 'next', () =>
        void api.queueNext(song.file).then(() => usePlayer.getState().refresh()),
      )}
      {entry('Add to Queue', 'list', () =>
        void api.queueAdd(song.file).then(() => usePlayer.getState().refresh()),
      )}
      {entry('Get Metadata', 'search', onGetMetadata)}
      {entry('Edit Info', 'gear', onEdit)}
      {entry('Tags…', 'tag', onTags)}
      {entry('Delete', 'x', onDelete, true)}
    </div>
  )
}
