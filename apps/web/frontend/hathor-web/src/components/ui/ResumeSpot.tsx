import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, deviceId, type PlaybackSpot, type Song } from '../../api/client'
import { engine } from '../../audio/engine'
import { formatTime, usePlayer } from '../../store/player'
import Icon from './icons'

// Resume across devices: another browser's paused spot, polled lightly
// (60s). The server excludes this browser's own device id, so a pause here
// never echoes back as a pill. Tapping plays that file here at its
// position — explicit only, nothing auto-plays. Hidden when nothing foreign
// is stashed, when it names what's already playing, or after tapping
// (self-dismisses until a newer spot arrives, via key on file+updatedUtc).
export function useResumeSpot(): PlaybackSpot | null {
  let id: string | null = null
  try {
    id = deviceId()
  } catch {
    id = null
  }
  const { data } = useQuery({
    queryKey: ['resume-state', id],
    queryFn: () => api.latestPlayback(id),
    refetchInterval: 60_000,
    staleTime: 30_000,
    retry: false,
  })
  return data ?? null
}

// Resolve the spot's file key to a library title (one cached request per
// spot); falls back to the raw file key when the library lacks it. Skipped
// while the pill is hidden so a matching track costs zero requests.
function useSpotSong(spot: PlaybackSpot, enabled: boolean) {
  return useQuery({
    queryKey: ['spot-song', spot.file, spot.isPodcast],
    queryFn: () =>
      spot.isPodcast ? api.podcastDetails(spot.file) : api.song(spot.file, false),
    enabled,
    staleTime: 5 * 60_000,
    retry: false,
  })
}

function spotName(song: Song | undefined, spot: PlaybackSpot): string {
  if (song && song.title && song.title !== 'Unknown') {
    const artist =
      song.artist && song.artist !== 'Unknown' ? ` — ${song.artist}` : ''
    return `${song.title}${artist}`
  }
  return spot.file
}

export default function ResumeSpot({ spot, compact = false }: { spot: PlaybackSpot; compact?: boolean }) {
  const playSong = usePlayer((s) => s.playSong)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const hidden = done || !spot || spot.file === currentFile
  const { data: song } = useSpotSong(spot, !hidden)
  if (hidden) return null

  const resume = () => {
    setBusy(true)
    const load = spot.isPodcast ? api.podcastDetails(spot.file) : api.song(spot.file, false)
    void load
      .then((song) =>
        playSong(
          song,
          [song],
          spot.isPodcast ? { type: 'podcast', id: null } : { type: 'all_songs', id: null },
        ),
      )
      .then(() => {
        engine.seek(spot.positionSec)
        return api.seek(spot.positionSec)
      })
      .then(
        () => setDone(true),
        () => setBusy(false),
      )
      .catch(() => setBusy(false))
  }

  const name = spotName(song, spot)
  const label = `Resume ${name} at ${formatTime(spot.positionSec)}${spot.device ? ` from ${spot.device}` : ''}`

  return (
    <button
      onClick={resume}
      disabled={busy}
      title={`${label} (saved ${spot.updatedUtc})`}
      className={
        compact
          ? 'min-w-0 max-w-44 flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-[0.15em] text-sky-300/90 hover:text-sky-200 transition-colors cursor-pointer disabled:opacity-50'
          : 'min-w-0 max-w-full flex items-center gap-2 px-4 py-2 rounded-full bg-sky-500/10 border border-sky-500/30 text-sky-200 text-sm hover:bg-sky-500/20 transition-colors cursor-pointer disabled:opacity-50'
      }
    >
      <Icon name="play" className={`${compact ? 'w-3 h-3' : 'w-4 h-4'} flex-shrink-0`} />
      <span className="truncate">
        {busy ? 'Loading…' : label}
      </span>
    </button>
  )
}
