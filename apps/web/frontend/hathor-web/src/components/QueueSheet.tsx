import { useEffect, useRef, useState } from 'react'
import { api, type PlayerState } from '../api/client'
import { usePlayer } from '../store/player'
import CoverArt from './ui/CoverArt'
import Icon from './ui/icons'
import Pagination from './ui/Pagination'
import SongMenu, { type SongMenuHandle } from './ui/SongMenu'

// Up Next queue panel (desktop #queue-sidebar): jump-to-position,
// drag-to-reorder, remove entries, move up/down, clear. Server owns queue
// order; every op refreshes the store so bar + sheet converge.
// Rows render paged (the store keeps the full list so jump/reorder/drop
// stay global); the pager follows the current song across page changes.
// Left-click jumps, right-click opens the song menu.
export const QUEUE_PAGE_SIZE = 50

export default function QueueSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queue = usePlayer((s) => s.queue)
  const queueTotal = usePlayer((s) => s.queueTotal)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const [dragFrom, setDragFrom] = useState<number | null>(null)
  const [dragOver, setDragOver] = useState<number | null>(null)
  const [page, setPage] = useState(1)
  const dragged = useRef(false)
  const menuRefs = useRef(new Map<string, SongMenuHandle | null>())
  const seenFile = useRef<string | null | undefined>(undefined)

  const totalPages = Math.max(1, Math.ceil(queue.length / QUEUE_PAGE_SIZE))
  const safePage = Math.min(Math.max(1, page), totalPages)

  // Follow the current song when it moves (track change, jump, reorder).
  useEffect(() => {
    if (currentFile === seenFile.current) return
    seenFile.current = currentFile ?? null
    if (!currentFile) return
    const idx = queue.findIndex((s) => s.file === currentFile)
    if (idx >= 0) setPage(Math.floor(idx / QUEUE_PAGE_SIZE) + 1)
  }, [currentFile, queue])

  const mutate = (p: Promise<PlayerState>) =>
    void p.then((s) => usePlayer.setState(s)).catch(() => {})

  if (!open) return null
  return (
    <div
      id="queue-sidebar"
      className="fixed right-0 bottom-28 top-0 w-full sm:w-80 border-l border-white/10 bg-zinc-950/95 backdrop-blur-3xl flex flex-col z-50 shadow-2xl"
    >
      <div className="p-5 border-b border-white/5 flex items-center justify-between">
        <h2 className="text-base font-semibold text-zinc-100 flex items-center gap-2">
          <Icon name="queue" className="w-4 h-4 text-orange-400" />
          Up Next{(queueTotal > 0 ? queueTotal : queue.length) > 0 && (
            <span className="text-xs font-medium text-zinc-500">
              {queueTotal > 0 ? queueTotal : queue.length}
            </span>
          )}
        </h2>
        <div className="flex items-center gap-2">
          {queue.length > 0 && (
            <button
              onClick={() => mutate(api.queueClear())}
              className="text-xs font-medium text-zinc-500 hover:text-orange-400 transition-colors uppercase tracking-wider cursor-pointer"
            >
              Clear
            </button>
          )}
          <button
            onClick={onClose}
            title="Close queue"
            className="text-zinc-400 hover:text-white p-1 rounded-full hover:bg-white/10 transition-colors cursor-pointer"
          >
            <Icon name="x" className="w-5 h-5" />
          </button>
        </div>
      </div>
      <div className="p-3 overflow-y-auto flex-1">
        <div id="queue-body" className="flex flex-col gap-1">
          {queue.length === 0 && <p className="text-sm text-zinc-500 text-center py-8">Queue is empty</p>}
          {queue
            .slice((safePage - 1) * QUEUE_PAGE_SIZE, safePage * QUEUE_PAGE_SIZE)
            .map((song, li) => {
              // Global index: jump/reorder/remove/drop all address the full queue.
              const i = (safePage - 1) * QUEUE_PAGE_SIZE + li
              return (
            <div
              key={`${song.file}-${i}`}
              draggable
              onDragStart={(e) => {
                e.dataTransfer.effectAllowed = 'move'
                try {
                  e.dataTransfer.setData('text/plain', String(i))
                } catch {
                  // Some browsers require setData for drag events to fire.
                }
                dragged.current = true
                setDragFrom(i)
              }}
              onDragEnd={() => {
                setDragFrom(null)
                setDragOver(null)
              }}
              onDragOver={(e) => {
                e.preventDefault()
                e.dataTransfer.dropEffect = 'move'
                if (dragOver !== i) setDragOver(i)
              }}
              onDrop={(e) => {
                e.preventDefault()
                const from = dragFrom
                setDragFrom(null)
                setDragOver(null)
                if (from !== null && from !== i) mutate(api.queueReorder(from, i))
              }}
              onClick={(e) => {
                if (dragged.current) {
                  dragged.current = false
                  return
                }
                if (e.detail > 1) return
                mutate(api.queueJump(i))
              }}
              onContextMenu={(e) => {
                e.preventDefault()
                menuRefs.current.get(`${song.file}-${i}`)?.openAt(e.clientX, e.clientY)
              }}
              title="Click to jump · right-click for actions · drag to reorder"
              className={`flex items-center gap-3 p-2 rounded-xl cursor-grab active:cursor-grabbing transition-all group ${
                dragFrom === i
                  ? 'opacity-40'
                  : song.file === currentFile
                    ? 'bg-orange-500/[0.06] ring-1 ring-orange-500/40'
                    : 'hover:bg-white/5'
              } ${dragOver === i && dragFrom !== null && dragFrom !== i ? 'ring-1 ring-orange-400/60' : ''}`}
            >
              <CoverArt src={song.coverArt} file={song.file} isPodcast={song.isPodcast} alt={song.title} className="w-10 h-10 rounded-lg flex-shrink-0 pointer-events-none" />
              <div className="flex-1 min-w-0 pointer-events-none">
                <div className="text-sm font-medium text-zinc-100 truncate group-hover:text-orange-400 transition-colors">
                  {song.title}
                </div>
                <div className="text-xs text-zinc-500 truncate">{song.artist}</div>
              </div>
              <div className="flex items-center gap-0.5 opacity-0 group-hover:opacity-100 transition-opacity">
                <button
                  title="Move up"
                  onClick={(e) => {
                    e.stopPropagation()
                    if (i > 0) mutate(api.queueReorder(i, i - 1))
                  }}
                  className="p-1.5 rounded-full text-zinc-500 hover:text-white hover:bg-white/10 cursor-pointer text-xs"
                >
                  ▲
                </button>
                <button
                  title="Move down"
                  onClick={(e) => {
                    e.stopPropagation()
                    if (i < queue.length - 1) mutate(api.queueReorder(i, i + 1))
                  }}
                  className="p-1.5 rounded-full text-zinc-500 hover:text-white hover:bg-white/10 cursor-pointer text-xs"
                >
                  ▼
                </button>
                <SongMenu
                  ref={(h) => {
                    menuRefs.current.set(`${song.file}-${i}`, h)
                  }}
                  song={song}
                  sourceType={song.isPodcast ? 'podcast' : 'all_songs'}
                />
                <button
                  title="Remove"
                  onClick={(e) => {
                    e.stopPropagation()
                    mutate(api.queueRemove(i))
                  }}
                  className="p-1.5 rounded-full text-zinc-500 hover:text-red-300 hover:bg-white/10 cursor-pointer"
                >
                  <Icon name="x" className="w-4 h-4 pointer-events-none" />
                </button>
              </div>
            </div>
              )
            })}
        </div>
      </div>
      {queue.length > QUEUE_PAGE_SIZE && (
        <div className="p-3 border-t border-white/5">
          <Pagination page={safePage} totalPages={totalPages} onChange={setPage} />
        </div>
      )}
    </div>
  )
}
