import type { ReactNode } from 'react'
import type { Song } from '../../api/client'
import { formatTime } from '../../store/player'
import CoverArt from './CoverArt'

// Standard 4-column song row (desktop table pattern): title+artist / album /
// duration + trailing actions slot (⋮ menu lands here in Phase 2).
// Active (playing) row gets the orange ring; click plays the song.
export const SONG_ROW_GRID = 'grid-cols-[minmax(0,4fr)_minmax(0,2.5fr)_100px_auto]'

export function VisualizerBars() {
  return (
    <div
      className="list-visualizer flex items-end justify-center gap-[2px] w-4 h-4 ml-1 flex-shrink-0 overflow-hidden"
      aria-hidden="true"
    >
      <div className="visualizer-bar" />
      <div className="visualizer-bar" />
      <div className="visualizer-bar" />
      <div className="visualizer-bar" />
    </div>
  )
}

export default function SongRow({
  song,
  active,
  playing = false,
  onPlay,
  actions,
  onArtistClick,
  onAlbumClick,
}: {
  song: Song
  active: boolean
  playing?: boolean
  onPlay: () => void
  actions?: ReactNode
  onArtistClick?: (artist: string) => void
  onAlbumClick?: (album: string) => void
}) {
  return (
    <div
      onClick={onPlay}
      className={`grid ${SONG_ROW_GRID} gap-4 items-center p-3 px-4 rounded-xl transition-all duration-300 cursor-pointer group ${
        active ? 'bg-orange-500/[0.06] ring-1 ring-orange-500/40' : 'hover:bg-white/5'
      }`}
    >
      <div className="flex items-center gap-3 min-w-0">
        <CoverArt src={song.coverArt} file={song.file} isPodcast={song.isPodcast} alt={song.title} className="w-10 h-10 rounded-lg flex-shrink-0" />
        <div className="min-w-0">
          <div className="flex items-center gap-1 min-w-0">
            <div
              className={`text-sm font-medium truncate transition-colors ${
                active ? 'text-orange-400' : 'text-zinc-100 group-hover:text-orange-400'
              }`}
            >
              {song.title}
            </div>
            {active && playing && <VisualizerBars />}
          </div>
          {onArtistClick && song.artist !== 'Unknown' ? (
            <button
              onClick={(e) => {
                e.stopPropagation()
                onArtistClick(song.artist)
              }}
              className="text-xs text-zinc-500 truncate hover:text-orange-400 transition-colors cursor-pointer"
            >
              {song.artist}
            </button>
          ) : (
            <div className="text-xs text-zinc-500 truncate">{song.artist}</div>
          )}
        </div>
      </div>
      {onAlbumClick && song.album !== 'Unknown' ? (
        <button
          onClick={(e) => {
            e.stopPropagation()
            onAlbumClick(song.album)
          }}
          className="text-sm text-zinc-400 truncate text-left hover:text-orange-400 transition-colors cursor-pointer"
        >
          {song.album}
        </button>
      ) : (
        <div className="text-sm text-zinc-400 truncate">{song.album}</div>
      )}
      <div className="text-sm text-zinc-500 text-right font-mono">{formatTime(song.duration)}</div>
      <div className="flex justify-end items-center gap-0.5 min-w-8">{actions}</div>
    </div>
  )
}
