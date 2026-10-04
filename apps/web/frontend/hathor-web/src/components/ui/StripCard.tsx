import { useRef } from 'react'
import type { Song } from '../../api/client'
import CoverArt from './CoverArt'
import Icon from './icons'
import SongMenu, { type SongMenuHandle } from './SongMenu'

// Home strip card (w-36 sm:w-44) with hover play overlay. Left-click plays;
// right-click opens the song menu at the cursor when menuSourceType is set.
export default function StripCard({
  song,
  onPlay,
  menuSourceType,
}: {
  song: Song
  onPlay: () => void
  menuSourceType?: string
}) {
  const menuRef = useRef<SongMenuHandle | null>(null)
  return (
    <div
      onClick={onPlay}
      onContextMenu={
        menuSourceType
          ? (e) => {
              e.preventDefault()
              menuRef.current?.openAt(e.clientX, e.clientY)
            }
          : undefined
      }
      className="group relative w-36 sm:w-44 flex-shrink-0 rounded-2xl overflow-hidden bg-zinc-900/40 border border-white/5 hover:border-orange-500/40 transition-all duration-300 cursor-pointer"
    >
      <div className="aspect-square w-full bg-zinc-800 overflow-hidden relative">
        <CoverArt
          src={song.coverArt}
          file={song.file}
          isPodcast={song.isPodcast}
          alt={song.title}
          className="w-full h-full"
          iconClassName="w-10 h-10 text-zinc-600"
          imgClassName="group-hover:scale-105 transition-transform duration-500"
        />
        <div className="absolute inset-0 bg-black/40 opacity-0 group-hover:opacity-100 transition-opacity duration-300 flex items-center justify-center">
          <div className="w-11 h-11 rounded-full bg-orange-500 hover:bg-orange-400 flex items-center justify-center text-white shadow-lg shadow-orange-500/40 transform translate-y-2 group-hover:translate-y-0 transition-transform duration-300">
            <Icon name="play" className="w-5 h-5 ml-0.5" />
          </div>
        </div>
      </div>
      <div className="p-3 flex flex-col min-w-0">
        <div className="text-sm font-medium text-zinc-100 truncate group-hover:text-orange-400 transition-colors">
          {song.title}
        </div>
        <div className="text-xs text-zinc-500 truncate mt-0.5">{song.artist}</div>
      </div>
      {menuSourceType && (
        <SongMenu
          ref={(h) => {
            menuRef.current = h
          }}
          song={song}
          sourceType={menuSourceType}
          hideButton
        />
      )}
    </div>
  )
}
