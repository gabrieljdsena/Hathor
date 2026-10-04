import { forwardRef, useCallback, useEffect, useId, useImperativeHandle, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useQueryClient } from '@tanstack/react-query'
import { api, type Song } from '../../api/client'
import { usePlayer } from '../../store/player'
import { evictCoverCache } from './CoverArt'
import Icon from './icons'
import Modal from './Modal'
import { claimMenu, releaseMenu, subscribeMenu } from './menuBus'
import { GhostButton, PrimaryButton } from './fields'
import EditSongModal from './EditSongModal'
import MetadataModal from './MetadataModal'
import SongPlaylistsModal from './SongPlaylistsModal'

export interface SongMenuHandle {
  openAt: (x: number, y: number) => void
}

// Bento ⋮ menu + right-click context menu (desktop row menu:
// Play, Play Next, Add to Queue, Edit Info, Add to Playlist…, Delete).
// Self-contained: owns Edit/Delete/Playlists dialogs, invalidates queries.
// Claims the global menu bus on open so only one menu shows at a time.
const SongMenu = forwardRef<SongMenuHandle, { song: Song; sourceType: string; sourceId?: string | null; hideButton?: boolean }>(function SongMenu(
  { song, sourceType, sourceId = null, hideButton = false },
  ref,
) {
  const menuId = useId()
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null)
  const [buttonMenu, setButtonMenu] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [metadataOpen, setMetadataOpen] = useState(false)
  const [playlistsOpen, setPlaylistsOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const queryClient = useQueryClient()
  const playSong = usePlayer((s) => s.playSong)
  const boxRef = useRef<HTMLDivElement>(null)

  const close = useCallback(() => {
    setPos(null)
    setButtonMenu(false)
    releaseMenu(menuId)
  }, [menuId])

  useImperativeHandle(
    ref,
    () => ({
      openAt: (x, y) => {
        claimMenu(menuId)
        setPos({ x, y })
      },
    }),
    [menuId],
  )

  // A menu claimed elsewhere closes this one (single open menu globally).
  useEffect(
    () =>
      subscribeMenu((open) => {
        if (open !== null && open !== menuId) {
          setPos(null)
          setButtonMenu(false)
        }
      }),
    [menuId],
  )

  useEffect(() => () => releaseMenu(menuId), [menuId])

  useEffect(() => {
    if (!pos && !buttonMenu) return
    const onDoc = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) close()
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close()
    }
    document.addEventListener('click', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('click', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [pos, buttonMenu, close])

  const refresh = () => {
    // Song metadata shows up under many keys (songs/all, artist-songs,
    // album-songs, daily-mix, playlist-songs, podcasts, recents, history):
    // invalidate everything so the edit is visible live on any view.
    void queryClient.invalidateQueries()
  }

  const playlistId = sourceType === 'playlist' && sourceId !== null ? Number(sourceId) : NaN
  const canRemoveFromPlaylist = sourceType === 'playlist' && Number.isFinite(playlistId)

  const items = [
    {
      key: 'play',
      label: 'Play',
      icon: 'play' as const,
      run: () => void playSong(song, [song], { type: sourceType, id: null }),
    },
    {
      key: 'next',
      label: 'Play Next',
      icon: 'next' as const,
      run: () =>
        void api
          .queueNext(song.file)
          .then(() => usePlayer.getState().refresh())
          .catch(() => {}),
    },
    {
      key: 'queue',
      label: 'Add to Queue',
      icon: 'list' as const,
      run: () =>
        void api
          .queueAdd(song.file)
          .then(() => usePlayer.getState().refresh())
          .catch(() => {}),
    },
    { key: 'edit', label: 'Edit Info', icon: 'gear' as const, run: () => { close(); setEditOpen(true) } },
    { key: 'metadata', label: 'Get Metadata', icon: 'search' as const, run: () => { close(); setMetadataOpen(true) } },
    ...(canRemoveFromPlaylist
      ? [
          {
            key: 'remove-playlist',
            label: 'Remove from playlist',
            icon: 'x' as const,
            danger: true,
            run: () =>
              void api
                .removeSongFromPlaylist(playlistId, song.file)
                .then(() => refresh())
                .catch(() => {}),
          },
        ]
      : []),
    { key: 'playlists', label: 'Add to Playlist…', icon: 'plus' as const, run: () => { close(); setPlaylistsOpen(true) } },
    { key: 'delete', label: 'Delete', icon: 'x' as const, danger: true, run: () => { close(); setDeleteOpen(true) } },
  ]

  const menu = (fixed: { x: number; y: number } | null) => (
    <div
      className={`${
        fixed ? 'fixed z-50' : 'absolute right-0 top-full mt-1 z-40'
      } w-48 py-1 bg-zinc-800 border border-white/10 rounded-xl shadow-2xl`}
      style={fixed ? { left: Math.min(fixed.x, window.innerWidth - 200), top: Math.min(fixed.y, window.innerHeight - 260) } : undefined}
    >
      {items.map((item) => (
        <button
          key={item.key}
          onClick={() => {
            close()
            item.run()
          }}
          className={`w-full text-left px-4 py-2 text-sm transition-colors flex items-center gap-2 cursor-pointer ${
            item.danger ? 'text-red-400 hover:text-red-300 hover:bg-white/5' : 'text-zinc-300 hover:bg-white/5 hover:text-white'
          }`}
        >
          <Icon name={item.icon} className="w-4 h-4 pointer-events-none" />
          {item.label}
        </button>
      ))}
    </div>
  )

  return (
    <div ref={boxRef} className="relative" onClick={(e) => e.stopPropagation()}>
      {!hideButton && (
        <button
          onClick={(e) => {
            e.stopPropagation()
            setPos(null)
            setButtonMenu((v) => {
              const next = !v
              if (next) claimMenu(menuId)
              else releaseMenu(menuId)
              return next
            })
          }}
          title="More actions"
          className="p-2 rounded-full text-zinc-600 hover:text-white hover:bg-white/10 transition-all focus:outline-none cursor-pointer"
        >
          <Icon name="dots" className="w-5 h-5 pointer-events-none" />
        </button>
      )}
      {buttonMenu && menu(null)}
      {/* Fixed (cursor) menus portal to body: ancestors with backdrop-blur
          or transforms would otherwise hijack fixed positioning. */}
      {pos && createPortal(menu(pos), document.body)}
      {editOpen && (
        <EditSongModal
          song={song}
          onClose={() => setEditOpen(false)}
          onSaved={() => {
            setEditOpen(false)
            refresh()
            // The edited file may be the current song — resync its display.
            void usePlayer.getState().refresh().catch(() => {})
          }}
        />
      )}
      {playlistsOpen && <SongPlaylistsModal song={song} onClose={() => setPlaylistsOpen(false)} />}
      {metadataOpen && <MetadataModal song={song} onClose={() => setMetadataOpen(false)} />}
      <Modal open={deleteOpen} onClose={() => setDeleteOpen(false)} title="Delete song?">
        <p className="text-sm text-zinc-400">
          <span className="text-zinc-200 font-medium">{song.title}</span> will be permanently removed from your
          library.
        </p>
        <div className="flex justify-end gap-3 pt-4 mt-4 border-t border-white/10">
          <GhostButton onClick={() => setDeleteOpen(false)}>Cancel</GhostButton>
          <PrimaryButton
            loading={deleting}
            onClick={() => {
              setDeleting(true)
              void api
                .deleteSong(song.file)
                .then(() => {
                  setDeleteOpen(false)
                  evictCoverCache(song.file)
                  refresh()
                  void usePlayer.getState().refresh()
                })
                .catch(() => setDeleteOpen(false))
                .finally(() => setDeleting(false))
            }}
          >
            {deleting ? 'Deleting…' : 'Yes, delete it'}
          </PrimaryButton>
        </div>
      </Modal>
    </div>
  )
})

export default SongMenu
