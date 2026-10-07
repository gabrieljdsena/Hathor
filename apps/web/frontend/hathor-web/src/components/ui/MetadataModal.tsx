import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, type ITunesHit, type Song } from '../../api/client'
import { usePlayer } from '../../store/player'
import { evictCoverCache } from './CoverArt'
import { LoadingState } from './states'
import Modal from './Modal'

// iTunes auto-tagging (desktop search_itunes_multi): candidate list with
// artwork preview; applying writes title/artist/album/year/genre + cover
// through the normal PATCH path. Missing iTunes fields keep existing values.
export default function MetadataModal({ song, onClose }: { song: Song; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [hits, setHits] = useState<ITunesHit[] | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [queued, setQueued] = useState(false)
  const [applying, setApplying] = useState(false)

  useEffect(() => {
    let cancelled = false
    setBusy(true)
    setError(null)
    api
      .itunesOptions(song.title, song.artist === 'Unknown' ? '' : song.artist)
      .then((r) => {
        if (!cancelled) setHits(r)
      })
      .catch((e: unknown) => {
        if (!cancelled) {
          setHits([])
          setError(e instanceof Error ? e.message : 'Metadata search failed.')
        }
      })
      .finally(() => {
        if (!cancelled) setBusy(false)
      })
    return () => {
      cancelled = true
    }
  }, [song.file, song.title, song.artist])

  const apply = (hit: ITunesHit) => {
    setApplying(true)
    setError(null)
    setQueued(false)
    void api
      .patchSong(song.file, {
        title: hit.title || null,
        artist: hit.artist || null,
        album: hit.album || null,
        year: hit.year || null,
        genre: hit.genre || null,
        coverArt: hit.artworkUrl || null,
      })
      .then((saved) => {
        evictCoverCache(song.file)
        void queryClient.invalidateQueries()
        void usePlayer.getState().refresh().catch(() => {})
        // Playing file: the tags are stashed, not written — say so instead
        // of closing as if they applied (the player-bar chip tracks it).
        if (saved.pending === true) {
          setQueued(true)
          setApplying(false)
          return
        }
        onClose()
      })
      .catch((e: unknown) => {
        setError(e instanceof Error ? e.message : 'Failed to apply metadata.')
        setApplying(false)
      })
  }

  return (
    <Modal open onClose={onClose} title="Get Metadata">
      <p className="text-sm text-zinc-400">
        iTunes candidates for <span className="text-zinc-200 font-medium">{song.title}</span>
        {song.artist !== 'Unknown' ? ` — ${song.artist}` : ''}. Applying replaces the tags.
      </p>
      {busy && <LoadingState label="Searching iTunes…" />}
      {error && <p className="text-sm text-red-400">{error}</p>}
      {queued && (
        <p className="text-sm text-orange-200 bg-orange-500/10 border border-orange-500/30 rounded-lg px-3 py-2">
          Saved — applies when this track changes (playing files keep gapless playback).
        </p>
      )}
      {!busy && !error && hits !== null && hits.length === 0 && (
        <p className="text-sm text-zinc-500">No candidates — try editing the title/artist first.</p>
      )}
      <div className="flex flex-col gap-2 mt-3 max-h-[50vh] overflow-y-auto">
        {(hits ?? []).map((h, i) => (
          <div key={`${h.title}-${h.artist}-${i}`} className="flex items-center gap-3 bg-white/5 rounded-xl p-3">
            {h.artworkUrl ? (
              // Plain img (not CoverArt): a broken remote preview must never
              // poison the file's cached cover.
              <img
                src={h.artworkUrl}
                alt=""
                className="w-12 h-12 rounded-lg flex-shrink-0 object-cover bg-zinc-800"
                loading="lazy"
                onError={(e) => {
                  e.currentTarget.style.display = 'none'
                }}
              />
            ) : (
              <div className="w-12 h-12 rounded-lg flex-shrink-0 bg-zinc-800" />
            )}
            <div className="flex-1 min-w-0">
              <div className="text-sm text-zinc-100 truncate">
                {h.title || '(untitled)'} — {h.artist || '(unknown artist)'}
              </div>
              <div className="text-xs text-zinc-500 truncate">
                {[h.album || null, h.year || null, h.genre || null].filter(Boolean).join(' • ') || 'No album info'}
              </div>
            </div>
            <button
              onClick={() => apply(h)}
              disabled={applying}
              className="px-3 py-1.5 rounded-lg bg-orange-500/15 text-orange-400 hover:bg-orange-500 hover:text-white text-xs font-medium transition-colors cursor-pointer disabled:opacity-50 flex-shrink-0"
            >
              {applying ? 'Applying…' : 'Apply'}
            </button>
          </div>
        ))}
      </div>
    </Modal>
  )
}
