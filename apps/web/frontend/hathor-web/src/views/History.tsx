import { useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { api, type HistoryItem } from '../api/client'
import { usePlayer } from '../store/player'
import CoverArt from '../components/ui/CoverArt'
import Icon from '../components/ui/icons'
import SongMenu, { type SongMenuHandle } from '../components/ui/SongMenu'
import { EmptyState, LoadingState } from '../components/ui/states'
import ViewHeader from '../components/ui/ViewHeader'

// History cloned from ui/views/history.html: Download / Played tabs,
// paginated rows (cover + title + artist + date + play action).
type Tab = 'download' | 'played'

function formatDate(iso: string | null) {
  if (!iso) return 'Unknown date'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? 'Unknown date' : d.toLocaleString()
}

export default function History() {
  const [tab, setTab] = useState<Tab>('download')
  const [page, setPage] = useState(1)
  const playSong = usePlayer((s) => s.playSong)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const isPlaying = usePlayer((s) => s.isPlaying)
  const navigate = useNavigate()

  const query =
    tab === 'download'
      ? useQuery({ queryKey: ['history-download', page], queryFn: () => api.downloadHistory(page, 10) })
      : useQuery({ queryKey: ['history-played', page], queryFn: () => api.playedHistory(page, 10) })

  const switchTab = (t: Tab) => {
    setTab(t)
    setPage(1)
  }

  const playRow = (item: HistoryItem) => {
    // History replay restarts the file (per-instance _historyId semantics).
    void playSong(item.song, [item.song], { type: 'all_songs', id: null })
  }
  const [queued, setQueued] = useState<string | null>(null)
  const redownload = (item: HistoryItem) => {
    if (!item.downloadedLink) return
    const key = `${item.song.file}-${item.datePlayed ?? item.dateDownload}`
    void api
      .redownload(item.downloadedLink, item.song.title, item.song.artist, item.song.isPodcast)
      .then(() => {
        setQueued(key)
        window.setTimeout(() => setQueued((k) => (k === key ? null : k)), 3000)
      })
      .catch(() => {})
  }
  const menuRefs = useRef(new Map<string, SongMenuHandle | null>())
  const rowKey = (item: HistoryItem) => `${item.song.file}-${item.datePlayed ?? item.dateDownload}`
  const openMenu = (item: HistoryItem, x: number, y: number) =>
    menuRefs.current.get(rowKey(item))?.openAt(x, y)

  return (
    <div className="flex flex-col items-center justify-start w-full pt-12 max-w-4xl mx-auto px-4">
      <div className="text-center mb-8">
        <ViewHeader icon="clock" title="History" subtitle="Your download and playback history" />
      </div>

      <div className="flex gap-4 sm:gap-8 border-b border-white/10 mb-6 w-full justify-center">
        {(['download', 'played'] as Tab[]).map((t) => (
          <button
            key={t}
            onClick={() => switchTab(t)}
            className={`cursor-pointer pb-3 border-b-2 transition-colors ${
              tab === t
                ? 'text-orange-400 border-orange-400 font-semibold'
                : 'text-zinc-400 border-transparent hover:text-white hover:border-white/30'
            }`}
          >
            {t === 'download' ? 'Download History' : 'Played History'}
          </button>
        ))}
      </div>

      <div className="w-full flex-grow pb-24">
        {query.isLoading ? (
          <LoadingState label="Loading history..." />
        ) : (query.data?.items ?? []).length === 0 ? (
          <EmptyState title="No history found." />
        ) : (
          <div className="flex flex-col gap-3">
            {query.data!.items.map((item) => {
              const active = currentFile === item.song.file
              return (
                <div
                  key={`${item.song.file}-${item.datePlayed ?? item.dateDownload}`}
                  onClick={() => playRow(item)}
                  onContextMenu={(e) => {
                    e.preventDefault()
                    openMenu(item, e.clientX, e.clientY)
                  }}
                  className={`flex items-center gap-4 bg-white/[0.04] border border-white/[0.08] rounded-xl p-3 hover:bg-white/[0.08] transition-all duration-300 cursor-pointer ${
                    active ? 'ring-1 ring-orange-500/40 bg-orange-500/[0.06]' : ''
                  }`}
                >
                  <CoverArt src={item.song.coverArt} file={item.song.file} isPodcast={item.song.isPodcast} alt={item.song.title} className="w-16 h-16 rounded-lg flex-shrink-0" />
                  <div className="flex-1 min-w-0">
                    <h4 className="text-zinc-100 font-medium truncate text-sm sm:text-base">{item.song.title}</h4>
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        navigate(`/artists/${encodeURIComponent(item.song.artist)}`)
                      }}
                      className="text-zinc-400 text-xs sm:text-sm truncate hover:text-orange-400 transition-colors cursor-pointer"
                    >
                      {item.song.artist}
                    </button>
                    <p className="text-zinc-600 text-xs mt-1">
                      {tab === 'download' ? 'Downloaded on' : 'Played on'}:{' '}
                      {formatDate(item.datePlayed ?? item.dateDownload)}
                    </p>
                  </div>
                  <button
                    title={active && isPlaying ? 'Pause' : 'Play'}
                    onClick={(e) => {
                      e.stopPropagation()
                      if (active) void usePlayer.getState().toggle()
                      else playRow(item)
                    }}
                    className="flex items-center justify-center w-10 h-10 rounded-full bg-orange-500/10 text-orange-400 hover:bg-orange-500 hover:text-white transition-colors shadow-md cursor-pointer flex-shrink-0"
                  >
                    <Icon name={active && isPlaying ? 'pause' : 'play'} className="w-5 h-5" />
                  </button>
                  {tab === 'download' && item.downloadedLink && (
                    <button
                      title={queued === rowKey(item) ? 'Queued for download' : 'Re-download from source'}
                      onClick={(e) => {
                        e.stopPropagation()
                        redownload(item)
                      }}
                      className={`flex items-center justify-center w-10 h-10 rounded-full transition-colors shadow-md cursor-pointer flex-shrink-0 ${
                        queued === rowKey(item)
                          ? 'bg-orange-500 text-white'
                          : 'bg-white/5 text-zinc-400 hover:bg-orange-500 hover:text-white'
                      }`}
                    >
                      <Icon name="download" className="w-5 h-5" />
                    </button>
                  )}
                  <SongMenu
                    ref={(h) => {
                      menuRefs.current.set(rowKey(item), h)
                    }}
                    song={item.song}
                    sourceType="all_songs"
                  />
                </div>
              )
            })}
          </div>
        )}

        {(query.data?.totalPages ?? 1) > 1 && (
          <div className="flex items-center justify-center gap-4 mt-8">
            <button
              disabled={page <= 1}
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              className="px-4 py-2 bg-white/5 border border-white/10 rounded-lg text-white transition-colors hover:bg-white/10 disabled:opacity-50 cursor-pointer"
            >
              Previous
            </button>
            <span className="text-zinc-400 text-sm">
              Page {query.data?.currentPage} of {query.data?.totalPages}
            </span>
            <button
              disabled={page >= (query.data?.totalPages ?? 1)}
              onClick={() => setPage((p) => p + 1)}
              className="px-4 py-2 bg-white/5 border border-white/10 rounded-lg text-white transition-colors hover:bg-white/10 disabled:opacity-50 cursor-pointer"
            >
              Next
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
