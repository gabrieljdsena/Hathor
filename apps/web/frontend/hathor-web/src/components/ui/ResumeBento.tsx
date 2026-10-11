import { useEffect, useId, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, type PlaybackSpot, type QueueSource, type Song } from '../../api/client'
import { engine } from '../../audio/engine'
import { formatTime, usePlayer } from '../../store/player'
import { parseServerDate } from '../../utils/dates'
import Icon from './icons'
import { claimMenu, releaseMenu, subscribeMenu } from './menuBus'

// Max queue files resolved on resume: the snapshot caps at 200, but each
// file costs one catalog request — resolve the head only, keep order.
export const RESUME_QUEUE_LIMIT = 50

function spotName(song: Song | undefined, spot: PlaybackSpot): string {
  if (song && song.title && song.title !== 'Unknown') {
    const artist =
      song.artist && song.artist !== 'Unknown' ? ` — ${song.artist}` : ''
    return `${song.title}${artist}`
  }
  return spot.file
}

function timeAgo(utc: string): string {
  const d = parseServerDate(utc)
  if (!d) return ''
  const mins = Math.max(0, Math.round((Date.now() - d.getTime()) / 60000))
  if (mins < 1) return 'just now'
  if (mins < 60) return `${mins}m ago`
  const hours = Math.round(mins / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}

// Restore a foreign spot's queue: resolve the snapshot's file keys to
// Songs (current track first, failures skipped), play them as one context,
// then seek to the saved position. Falls back to the single file when the
// snapshot is empty (spots written by older clients).
export async function resumeFromSpot(
  spot: PlaybackSpot,
  playSong: (song: Song, context: Song[], source: QueueSource | null) => Promise<unknown>,
): Promise<void> {
  const seen = new Set<string>()
  const ordered: string[] = []
  for (const f of [spot.file, ...(spot.queueFiles ?? [])]) {
    if (!f || seen.has(f.toLowerCase())) continue
    seen.add(f.toLowerCase())
    ordered.push(f)
    if (ordered.length >= RESUME_QUEUE_LIMIT) break
  }
  const settled = await Promise.allSettled(
    ordered.map((f) =>
      spot.isPodcast ? api.podcastDetails(f) : api.song(f, false),
    ),
  )
  const songs = settled.flatMap((r) => (r.status === 'fulfilled' ? [r.value] : []))
  // Current file must resolve — otherwise there is nothing to play.
  const current = songs.find((s) => s.file.toLowerCase() === spot.file.toLowerCase()) ?? songs[0]
  if (!current) throw new Error('Track no longer in library')
  const context = songs.some((s) => s.file === current.file) ? songs : [current, ...songs]
  const source: QueueSource =
    spot.queueSource && spot.queueSource.type
      ? { type: spot.queueSource.type, id: spot.queueSource.id ?? null }
      : { type: spot.isPodcast ? 'podcast' : 'all_songs', id: null }
  await playSong(current, context, source)
  engine.seek(spot.positionSec)
  await api.seek(spot.positionSec)
}

// Resume-from-another-device bento (dark + orange): a dots button in the
// player bar / now-playing top bar. Opens a small menu that lists the
// DEVICE first, then the song + saved position. Explicit tap only —
// nothing auto-plays. Hidden when there is no foreign spot or it names
// what is already playing.
export default function ResumeBento({ spot }: { spot: PlaybackSpot | null }) {
  const menuId = useId()
  const currentFile = usePlayer((s) => s.currentSong?.file)
  const playSong = usePlayer((s) => s.playSong)
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const boxRef = useRef<HTMLDivElement>(null)

  const hidden = done || !spot || spot.file === currentFile
  // Song lookup runs only while visible (a matching track costs zero
  // requests while hidden).
  const { data: resolved } = useQuery({
    queryKey: ['spot-song', spot?.file, spot?.isPodcast],
    queryFn: () =>
      spot!.isPodcast ? api.podcastDetails(spot!.file) : api.song(spot!.file, false),
    enabled: !hidden && !!spot,
    staleTime: 5 * 60_000,
    retry: false,
  })

  // Single open menu globally (same bus as SongMenu).
  useEffect(
    () =>
      subscribeMenu((id) => {
        if (id !== null && id !== menuId) setOpen(false)
      }),
    [menuId],
  )
  useEffect(() => () => releaseMenu(menuId), [menuId])

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) {
        setOpen(false)
        releaseMenu(menuId)
      }
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false)
        releaseMenu(menuId)
      }
    }
    document.addEventListener('click', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('click', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open, menuId])

  if (hidden || !spot) return null
  const name = spotName(resolved, spot)
  const device = spot.device || spot.userKey || 'another device'
  const ago = timeAgo(spot.updatedUtc)

  const toggle = () => {
    setOpen((v) => {
      const next = !v
      if (next) {
        setError(null)
        claimMenu(menuId)
      } else releaseMenu(menuId)
      return next
    })
  }

  const resume = () => {
    if (busy) return
    setBusy(true)
    setError(null)
    void resumeFromSpot(spot, playSong).then(
      () => {
        setBusy(false)
        setDone(true)
        setOpen(false)
        releaseMenu(menuId)
      },
      (e: unknown) => {
        setBusy(false)
        setError(e instanceof Error ? e.message : 'Resume failed')
      },
    )
  }

  return (
    <div ref={boxRef} className="relative flex-shrink-0" onClick={(e) => e.stopPropagation()}>
      <button
        onClick={toggle}
        title={`Resume from ${device}: ${name} at ${formatTime(spot.positionSec)}`}
        aria-label="Resume from another device"
        aria-expanded={open}
        className={`relative cursor-pointer transition-colors p-2 rounded-full focus:outline-none ${
          open ? 'text-orange-300 bg-orange-500/15' : 'text-orange-400/90 hover:text-orange-300 hover:bg-orange-500/10'
        }`}
      >
        <Icon name="dots" className="w-5 h-5" />
        <span aria-hidden="true" className="absolute top-1.5 right-1.5 w-1.5 h-1.5 rounded-full bg-orange-400" />
      </button>
      {open && (
        <div
          role="menu"
          aria-label="Resume from another device"
          className="absolute bottom-full mb-2 right-0 z-[90] w-72 overflow-hidden rounded-2xl bg-zinc-900 border border-orange-500/20 shadow-2xl"
        >
          <div className="px-4 pt-3 pb-2 border-b border-white/5">
            <div className="text-[10px] font-semibold uppercase tracking-[0.2em] text-orange-400/90 truncate">
              From {device}
            </div>
            {ago && <div className="text-[11px] text-zinc-500 mt-0.5">{ago}</div>}
          </div>
          <div className="px-4 py-3">
            <div className="text-sm font-medium text-zinc-100 truncate">{name}</div>
            <div className="text-xs text-zinc-500 mt-0.5">
              Saved at {formatTime(spot.positionSec)}
              {spot.isPodcast ? ' · podcast' : ''}
            </div>
          </div>
          {error && (
            <p className="mx-4 mb-2 text-xs text-red-300 bg-red-500/10 border border-red-500/30 rounded-lg px-3 py-2">
              {error}
            </p>
          )}
          <div className="px-3 pb-3">
            <button
              onClick={resume}
              disabled={busy}
              className="w-full px-4 py-2 rounded-xl bg-orange-500 hover:bg-orange-400 text-white text-sm font-semibold transition-colors cursor-pointer disabled:opacity-50"
            >
              {busy ? 'Loading…' : `Resume at ${formatTime(spot.positionSec)}`}
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
