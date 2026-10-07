import { useEffect, useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type LyricsHit } from '../api/client'
import { usePlayer } from '../store/player'
import Icon from './ui/icons'
import { LoadingState } from './ui/states'
import SyncedLyrics, { LYRICS_OFFSET_STEP_MS, useLyricsOffset, useSongLyrics } from './ui/SyncedLyrics'
import { useActiveChapter } from './ui/PodcastTimestamps'

// Timing correction beside Romaji: −/+ steppers (250ms), numeric input
// with native arrows, persisted server-side per song (0 = none, deleted).
function OffsetControl({
  offsetMs,
  onStep,
  onCommit,
}: {
  offsetMs: number
  onStep: (deltaMs: number) => void
  onCommit: (ms: number) => void
}) {
  const [draft, setDraft] = useState<string | null>(null)
  const shown = draft ?? String(offsetMs)
  const commit = (raw: string) => {
    setDraft(null)
    const n = Number.parseInt(raw, 10)
    onCommit(Number.isFinite(n) ? n : 0)
  }
  return (
    <div
      className="flex items-center gap-1 px-2 py-1 rounded-full bg-white/5"
      title="Shift lyric timing: positive delays the highlight, negative advances it"
    >
      <button
        onClick={() => onStep(-LYRICS_OFFSET_STEP_MS)}
        title="Retard 250ms"
        aria-label="Retard lyrics by 250 milliseconds"
        className="w-6 h-6 rounded-full text-zinc-400 hover:text-white hover:bg-white/10 transition-colors cursor-pointer text-sm leading-none"
      >
        −
      </button>
      <input
        type="number"
        value={shown}
        step={LYRICS_OFFSET_STEP_MS}
        aria-label="Lyrics timing offset in milliseconds"
        onChange={(e) => setDraft(e.target.value)}
        onBlur={(e) => commit(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit((e.target as HTMLInputElement).value)
        }}
        className="w-16 bg-transparent text-center text-xs font-mono text-zinc-200 outline-none"
      />
      <button
        onClick={() => onStep(LYRICS_OFFSET_STEP_MS)}
        title="Forward 250ms"
        aria-label="Forward lyrics by 250 milliseconds"
        className="w-6 h-6 rounded-full text-zinc-400 hover:text-white hover:bg-white/10 transition-colors cursor-pointer text-sm leading-none"
      >
        +
      </button>
      <span className="text-[10px] text-zinc-500 pr-1">ms</span>
    </div>
  )
}

// Lyrics overlay (desktop #lyrics-container-view): synced lines highlight
// with playback, click seeks; romaji toggle via /lyrics/romanize; full
// search / replace / remove flow for wrong or missing lyrics.
export default function LyricsSheet({
  open,
  onClose,
  queueOpen = false,
}: {
  open: boolean
  onClose: () => void
  // Queue panel overlays the right side (w-80 on sm+): shrink the lyrics
  // view so it stays readable instead of hiding underneath.
  queueOpen?: boolean
}) {
  const song = usePlayer((s) => s.currentSong)
  const [romaji, setRomaji] = useState(false)
  const [showSearch, setShowSearch] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  // Podcast chapter lyrics: track = active chapter name, artist always
  // empty (track-only search). Chapter flips refetch under a new query key;
  // the loading state covers the swap so highlight and base never mismatch.
  const chapter = useActiveChapter(song?.file, song?.isPodcast)
  const { data: lyrics, isLoading } = useSongLyrics(
    song?.file,
    chapter?.name ?? song?.title ?? '',
    chapter ? '' : (song?.artist ?? ''),
    song?.duration,
    open,
    chapter?.id ?? null,
  )

  // Short content hash for the romanization key: the raw synced/plain
  // payloads can be kilobytes, and query keys are serialized + retained.
  const lyricsHash = useMemo(() => {
    const text = `${lyrics?.synced?.length ?? 0}:${lyrics?.plain?.length ?? 0}:${lyrics?.synced?.slice(0, 64) ?? ''}${lyrics?.plain?.slice(0, 64) ?? ''}`
    let h = 0
    for (let i = 0; i < text.length; i++) h = (Math.imul(h, 31) + text.charCodeAt(i)) | 0
    return h.toString(36)
  }, [lyrics?.synced, lyrics?.plain])

  const { data: romanized } = useQuery({
    queryKey: ['lyrics-roman', song?.file, lyricsHash],
    queryFn: async () => {
      const [s, p] = await Promise.all([
        lyrics?.synced ? api.romanize(lyrics.synced, true).then((r) => r.text) : null,
        lyrics?.plain ? api.romanize(lyrics.plain, false).then((r) => r.text) : null,
      ])
      return { synced: s, plain: p }
    },
    enabled: open && romaji && !!lyrics && (!!lyrics.synced || !!lyrics.plain),
  })

  const synced = romaji && romanized?.synced ? romanized.synced : lyrics?.synced
  const plain = romaji && romanized?.plain ? romanized.plain : lyrics?.plain
  const hasLyrics = !!synced || !!plain
  const { offsetMs, setOffset } = useLyricsOffset(song?.file)

  if (!open) return null
  return (
    <div
      id="lyrics-container-view"
      className={`absolute inset-x-0 top-0 bottom-0 z-40 bg-zinc-950/95 backdrop-blur-3xl flex-col items-center overflow-hidden flex transition-[right] duration-300 ${
        queueOpen ? 'sm:right-80' : ''
      }`}
    >
      <div className="absolute top-0 inset-x-0 z-10 flex items-center justify-between px-5 sm:px-8 h-16">
        <button
          onClick={onClose}
          title="Back to Now Playing"
          className="flex items-center gap-2 text-zinc-400 hover:text-white transition-all duration-200 px-3 py-2 rounded-xl hover:bg-white/10 cursor-pointer"
        >
          <Icon name="x" className="w-5 h-5" />
          <span className="text-xs font-semibold uppercase tracking-[0.2em] hidden sm:inline">Now Playing</span>
        </button>
        <div className="flex items-center gap-2">
          <span className="text-xs text-zinc-500 uppercase tracking-widest hidden sm:inline">
            {song ? `${song.title} • ${song.artist}` : ''}
          </span>
          {song && (
            <button
              onClick={() => {
                setNotice(null)
                setShowSearch((v) => !v)
              }}
              title={hasLyrics ? 'Search / replace lyrics' : 'Search lyrics'}
              className={`px-3 py-1.5 rounded-full text-xs font-semibold transition-all cursor-pointer ${
                showSearch ? 'bg-orange-500 text-white' : 'bg-white/5 text-zinc-400 hover:text-white'
              }`}
            >
              {hasLyrics ? 'Replace' : 'Search'}
            </button>
          )}
          {song && (
            <OffsetControl
              offsetMs={offsetMs}
              onStep={(d) => void setOffset(offsetMs + d)}
              onCommit={(v) => void setOffset(v)}
            />
          )}
          <button
            onClick={() => setRomaji((v) => !v)}
            title="Toggle romaji"
            className={`px-3 py-1.5 rounded-full text-xs font-semibold transition-all cursor-pointer ${
              romaji ? 'bg-orange-500 text-white' : 'bg-white/5 text-zinc-400 hover:text-white'
            }`}
          >
            Romaji
          </button>
        </div>
      </div>

      <div className="relative w-full max-w-[88rem] mx-auto flex-1 min-h-0 px-4 pt-20 pb-24 flex flex-col">
        {!song ? (
          <p className="text-zinc-500 text-xl font-semibold text-center">Select a song to load lyrics...</p>
        ) : showSearch ? (
          <LyricsSearchPanel
            songFile={song.file}
            initialTrack={chapter?.name ?? song.title}
            initialArtist={chapter ? '' : song.artist === 'Unknown' ? '' : song.artist}
            durationSec={song.duration}
            hasLyrics={hasLyrics}
            notice={notice}
            setNotice={setNotice}
            onDone={() => setShowSearch(false)}
          />
        ) : (
          <SyncedLyrics
            synced={synced}
            plain={plain}
            isLoading={isLoading}
            offsetMs={offsetMs}
            chapterStartSecs={chapter?.startSecs ?? 0}
            emptySlot={
              <div className="flex flex-col items-center gap-4">
                <p className="text-zinc-500 text-xl font-semibold">No lyrics found.</p>
                <button
                  onClick={() => {
                    setNotice(null)
                    setShowSearch(true)
                  }}
                  className="px-4 py-2 rounded-lg bg-white/5 text-zinc-300 hover:text-white hover:bg-white/10 text-sm cursor-pointer"
                >
                  Search candidates
                </button>
              </div>
            }
          />
        )}
      </div>
    </div>
  )
}

function errMsg(e: unknown): string {
  if (!(e instanceof Error)) return 'Request failed.'
  try {
    const parsed = JSON.parse(e.message) as { message?: unknown }
    if (typeof parsed.message === 'string' && parsed.message.length > 0) return parsed.message
  } catch {
    // Plain-text error already.
  }
  return e.message
}

function LyricsSearchPanel({
  songFile,
  initialTrack,
  initialArtist,
  durationSec,
  hasLyrics,
  notice,
  setNotice,
  onDone,
}: {
  songFile: string
  initialTrack: string
  initialArtist: string
  durationSec: number | undefined
  hasLyrics: boolean
  notice: string | null
  setNotice: (m: string | null) => void
  onDone: () => void
}) {
  const queryClient = useQueryClient()
  const [track, setTrack] = useState(initialTrack)
  const [artist, setArtist] = useState(initialArtist)
  const [hits, setHits] = useState<LyricsHit[] | null>(null)
  const [searching, setSearching] = useState(false)
  const [savingId, setSavingId] = useState<number | null>(null)
  const [previewId, setPreviewId] = useState<number | null>(null)
  const [busyAction, setBusyAction] = useState<string | null>(null)

  // Reset when the song changes.
  useEffect(() => {
    setTrack(initialTrack)
    setArtist(initialArtist)
    setHits(null)
    setPreviewId(null)
    setNotice(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [songFile])

  const refreshLyrics = () => queryClient.invalidateQueries({ queryKey: ['lyrics', songFile] })

  const search = (t: string, a: string) => {
    if (!t.trim()) {
      setNotice('Enter a track name to search.')
      return
    }
    setSearching(true)
    setNotice(null)
    api
      .searchLyrics(t.trim(), a.trim())
      .then((r) => {
        setHits(r)
        if (r.length === 0) setNotice('No candidates — try different spelling or remove the artist.')
      })
      .catch((e: unknown) => {
        setHits([])
        setNotice(errMsg(e))
      })
      .finally(() => setSearching(false))
  }

  // Initial search with the song's own metadata (previous behavior).
  useEffect(() => {
    search(initialTrack, initialArtist)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [songFile])

  const save = (hit: LyricsHit) => {
    setSavingId(hit.id)
    setNotice(null)
    void api
      .saveLyrics(songFile, hit.syncedLyrics, hit.plainLyrics)
      .then(() => {
        refreshLyrics()
        setNotice(`Saved “${hit.trackName} — ${hit.artistName}”.`)
        onDone()
      })
      .catch((e: unknown) => setNotice(errMsg(e)))
      .finally(() => setSavingId(null))
  }

  const refetch = () => {
    setBusyAction('refetch')
    setNotice(null)
    void api
      .songLyrics(songFile, track.trim() || initialTrack, artist.trim(), durationSec, true)
      .then((fresh) => {
        queryClient.setQueryData(['lyrics', songFile], fresh)
        setNotice('Re-fetched from the provider.')
        onDone()
      })
      .catch((e: unknown) => setNotice(errMsg(e)))
      .finally(() => setBusyAction(null))
  }

  const remove = () => {
    setBusyAction('remove')
    setNotice(null)
    void api
      .deleteLyrics(songFile)
      .then(() => {
        refreshLyrics()
        setNotice('Saved lyrics removed — re-fetching from the provider.')
        onDone()
      })
      .catch((e: unknown) => setNotice(errMsg(e)))
      .finally(() => setBusyAction(null))
  }

  const inputClass =
    'w-full px-3 py-2 text-sm text-zinc-200 bg-white/5 border border-white/10 outline-none focus:border-orange-500 rounded-lg placeholder:text-zinc-600'

  return (
    <div className="w-full max-w-2xl mx-auto flex flex-col gap-3 overflow-y-auto pb-8">
      <div className="flex flex-col sm:flex-row gap-2">
        <input
          value={track}
          onChange={(e) => setTrack(e.target.value)}
          placeholder="Track name"
          aria-label="Track name"
          className={inputClass}
        />
        <input
          value={artist}
          onChange={(e) => setArtist(e.target.value)}
          placeholder="Artist (optional)"
          aria-label="Artist"
          className={inputClass}
        />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <button
          onClick={() => search(track, artist)}
          disabled={searching}
          className="px-4 py-2 rounded-lg bg-orange-500 hover:bg-orange-400 text-white text-sm font-medium transition-colors cursor-pointer disabled:opacity-50"
        >
          {searching ? 'Searching…' : 'Search'}
        </button>
        <button
          onClick={refetch}
          disabled={busyAction !== null}
          title="Skip the cache and fetch again from the provider"
          className="px-4 py-2 rounded-lg bg-white/5 text-zinc-300 hover:text-white hover:bg-white/10 text-sm transition-colors cursor-pointer disabled:opacity-50"
        >
          {busyAction === 'refetch' ? 'Fetching…' : 'Re-fetch'}
        </button>
        {hasLyrics && (
          <button
            onClick={remove}
            disabled={busyAction !== null}
            title="Delete the saved lyrics so the next view re-fetches"
            className="px-4 py-2 rounded-lg bg-white/5 text-red-400 hover:text-red-300 hover:bg-red-500/10 text-sm transition-colors cursor-pointer disabled:opacity-50"
          >
            {busyAction === 'remove' ? 'Removing…' : 'Remove saved'}
          </button>
        )}
        <button
          onClick={onDone}
          className="ml-auto px-4 py-2 rounded-lg text-zinc-500 hover:text-white text-sm transition-colors cursor-pointer"
        >
          Close
        </button>
      </div>
      {notice && (
        <p className="text-sm text-orange-200 bg-orange-500/10 border border-orange-500/30 rounded-lg px-3 py-2">
          {notice}
        </p>
      )}
      {searching && <LoadingState label="Searching…" />}
      {!searching && hits !== null && hits.length === 0 && !notice && (
        <p className="text-sm text-zinc-500">No candidates.</p>
      )}
      <div className="flex flex-col gap-2 text-left">
        {(hits ?? []).map((h) => (
          <div key={h.id} className="flex flex-col gap-2 bg-white/5 rounded-xl p-3">
            <div className="flex items-center gap-3">
              <div className="flex-1 min-w-0">
                <div className="text-sm text-zinc-100 truncate">
                  {h.trackName} — {h.artistName}
                </div>
                <div className="text-xs text-zinc-500">
                  {h.syncedLyrics ? 'Synced' : 'Plain'} available
                  {h.albumName ? ` · ${h.albumName}` : ''}
                </div>
              </div>
              <button
                onClick={() => setPreviewId(previewId === h.id ? null : h.id)}
                className="px-3 py-1.5 rounded-lg bg-white/5 text-zinc-300 hover:text-white hover:bg-white/10 text-xs font-medium transition-colors cursor-pointer"
              >
                {previewId === h.id ? 'Hide' : 'Preview'}
              </button>
              <button
                onClick={() => save(h)}
                disabled={savingId !== null}
                className="px-3 py-1.5 rounded-lg bg-orange-500/15 text-orange-400 hover:bg-orange-500 hover:text-white text-xs font-medium transition-colors cursor-pointer disabled:opacity-50"
              >
                {savingId === h.id ? 'Saving…' : hasLyrics ? 'Replace' : 'Save'}
              </button>
            </div>
            {previewId === h.id && (
              <pre className="text-xs text-zinc-300 whitespace-pre-wrap max-h-48 overflow-y-auto bg-black/40 rounded-lg p-3 font-sans">
                {h.syncedLyrics ?? h.plainLyrics ?? '(empty)'}
              </pre>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
