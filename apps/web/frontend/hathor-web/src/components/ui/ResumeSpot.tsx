import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, type PlaybackSpot } from '../../api/client'
import { engine } from '../../audio/engine'
import { formatTime, usePlayer } from '../../store/player'
import Icon from './icons'

// Resume across devices: another device's paused spot, polled lightly
// (60s). Tapping plays that file here at its position — explicit only,
// nothing auto-plays. Hidden when nothing foreign is stashed, when it names
// what's already playing, or after tapping (self-dismisses until a newer
// spot arrives, via key on file+updatedUtc).
export function useResumeSpot(): PlaybackSpot | null {
  const { data } = useQuery({
    queryKey: ['resume-state'],
    queryFn: api.latestPlayback,
    refetchInterval: 60_000,
    staleTime: 30_000,
    retry: false,
  })
  return data ?? null
}

export default function ResumeSpot({ spot, compact = false }: { spot: PlaybackSpot; compact?: boolean }) {
  const playSong = usePlayer((s) => s.playSong)
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  if (done || !spot || spot.file === currentFile) return null

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

  return (
    <button
      onClick={resume}
      disabled={busy}
      title={`Continue from ${spot.device ?? 'another device'} (saved ${spot.updatedUtc})`}
      className={
        compact
          ? 'text-[10px] font-semibold uppercase tracking-[0.15em] text-sky-300/90 hover:text-sky-200 transition-colors cursor-pointer disabled:opacity-50'
          : 'flex items-center gap-2 px-4 py-2 rounded-full bg-sky-500/10 border border-sky-500/30 text-sky-200 text-sm hover:bg-sky-500/20 transition-colors cursor-pointer disabled:opacity-50'
      }
    >
      <Icon name="play" className={compact ? 'w-3 h-3' : 'w-4 h-4'} />
      <span className="truncate max-w-64">
        {busy ? 'Loading…' : `Resume ${spot.file} at ${formatTime(spot.positionSec)}`}
        {!compact && spot.device ? ` from ${spot.device}` : ''}
      </span>
    </button>
  )
}
