import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../api/client'
import { engine } from '../../audio/engine'
import { activeLrcIndex, parseLrc } from '../../lyrics'

// Shared synced-lyrics renderer (desktop renderLyrics + updateHighlightedLine):
// big lines (text-3xl md:text-5xl), active line white + scaled, click seeks.
// Owns its scroll container so active-line scrolling stays precise
// (scrollIntoView would shift the whole page). Used by the lyrics overlay
// and the fullscreen Now Playing pane.

export function useSongLyrics(
  file: string | undefined,
  title: string,
  artist: string,
  durationSec: number | undefined,
  enabled: boolean,
) {
  return useQuery({
    queryKey: ['lyrics', file],
    queryFn: () => api.songLyrics(file!, title, artist, durationSec),
    enabled: enabled && !!file,
    retry: false,
  })
}

export function useAudioPosition(): number {
  const [position, setPosition] = useState(0)
  useEffect(() => {
    const id = window.setInterval(() => {
      setPosition(engine.time())
    }, 250)
    return () => window.clearInterval(id)
  }, [])
  return position
}

export const LYRICS_OFFSET_STEP_MS = 250;
export const LYRICS_OFFSET_LIMIT_MS = 10000;

// Server-persisted per-song highlight timing correction (sparse: the API
// deletes the row when the offset returns to 0). Shared by the header
// control and the highlight math below through one query key.
export function useLyricsOffset(file: string | null | undefined) {
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: ['lyrics-offset', file ?? ''],
    queryFn: () => api.lyricsOffset(file!),
    enabled: !!file,
    retry: false,
    staleTime: 1000 * 60 * 5,
  })
  const setOffset = (ms: number) => {
    if (!file) return Promise.resolve(0)
    const clamped = Math.min(
      LYRICS_OFFSET_LIMIT_MS,
      Math.max(-LYRICS_OFFSET_LIMIT_MS, Math.round(ms) || 0),
    )
    queryClient.setQueryData(['lyrics-offset', file], { offsetMs: clamped })
    return api
      .setLyricsOffset(file, clamped)
      .then((r) => {
        queryClient.setQueryData(['lyrics-offset', file], r)
        return r.offsetMs
      })
      .catch(() => {
        void queryClient.invalidateQueries({ queryKey: ['lyrics-offset', file] })
        return query.data?.offsetMs ?? 0
      })
  }
  return { offsetMs: query.data?.offsetMs ?? 0, setOffset }
}

export default function SyncedLyrics({
  synced,
  plain,
  isLoading,
  offsetMs = 0,
  className = '',
  loadingLabel = 'Loading lyrics…',
  emptySlot,
}: {
  synced: string | null | undefined
  plain: string | null | undefined
  isLoading: boolean
  // Highlight timing correction, milliseconds (server-persisted per song).
  offsetMs?: number
  className?: string
  loadingLabel?: string
  emptySlot?: ReactNode
}) {
  const lines = useMemo(() => parseLrc(synced ?? null), [synced])
  const position = useAudioPosition()

  const activeIdx = activeLrcIndex(lines, position + offsetMs / 1000)
  const listRef = useRef<HTMLDivElement>(null)
  const activeRef = useRef<HTMLParagraphElement>(null)

  useEffect(() => {
    const container = listRef.current
    const active = activeRef.current
    if (!container || !active) return
    const top = active.offsetTop - container.clientHeight / 2 + active.clientHeight / 2
    container.scrollTo({ top: Math.max(0, top), behavior: 'smooth' })
  }, [activeIdx])

  if (isLoading) {
    return <p className="text-zinc-500 text-2xl md:text-4xl font-semibold animate-pulse">{loadingLabel}</p>
  }

  return (
    <div
      ref={listRef}
      className={`relative w-full text-center space-y-6 md:space-y-10 overflow-y-auto overflow-x-hidden h-full scroll-smooth custom-scrollbar ${className}`}
      style={{
        WebkitMaskImage: 'linear-gradient(to bottom, black 85%, transparent 100%)',
        maskImage: 'linear-gradient(to bottom, black 85%, transparent 100%)',
      }}
    >
      {lines.length > 0 ? (
        <>
          {lines.map((line, i) => (
            <p
              key={`${line.timeSec}-${i}`}
              ref={i === activeIdx ? activeRef : undefined}
              onClick={() => {
                engine.seek(line.timeSec)
                void api.seek(line.timeSec).catch(() => {})
              }}
              className={`lyric-line font-bold cursor-pointer transition-all duration-300 text-4xl sm:text-5xl lg:text-6xl xl:text-7xl ${
                i === activeIdx ? 'text-white scale-105' : 'text-zinc-600 hover:text-zinc-400'
              }`}
            >
              {line.text || '♪'}
            </p>
          ))}
          {/* Trailing space so the last lines can scroll to center instead
              of sticking under the bottom fade. */}
          <div aria-hidden="true" className="h-[35vh] flex-shrink-0" />
        </>
      ) : plain ? (
        <div className="text-zinc-300 text-xl md:text-2xl font-semibold whitespace-pre-line leading-relaxed">
          {plain}
        </div>
      ) : (
        (emptySlot ?? <p className="text-zinc-500 text-2xl md:text-4xl font-semibold">No lyrics found.</p>)
      )}
    </div>
  )
}
