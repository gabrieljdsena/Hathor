import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type PodcastTimestamp } from '../../api/client'
import { engine } from '../../audio/engine'
import { formatTime, usePlayer } from '../../store/player'
import Icon from './icons'
import Modal from './Modal'
import ToggleSwitch from './ToggleSwitch'
import { GhostButton, PrimaryButton, TextField } from './fields'
import { LoadingState } from './states'
import {
  activeChapterAt,
  autoSkipTarget,
  formatChapterTime,
  nextChapterStart,
  parseChapterInput,
  prevChapterStart,
  sortChapters,
} from '../../utils/timestamps'

function chapterKey(file: string) {
  return ['podcast-timestamps', file]
}

// Shared chapter list for an episode (manager, skip strip, transport all
// read the same cache key). Disabled without a file.
export function usePodcastChapters(file: string | null | undefined) {
  return useQuery({
    queryKey: ['podcast-timestamps', file ?? ''],
    queryFn: () => api.podcastTimestamps(file as string),
    enabled: !!file,
  })
}

function seekTo(secs: number) {
  engine.seek(secs)
  void api.seek(secs).catch(() => {})
}

// Chapter-boundary transport for the current podcast episode. Returns true
// when a chapter seek happened (caller falls back to prev/next track).
// Works whether or not auto-skip is enabled.
export function useChapterJump() {  const file = usePlayer((s) => s.currentSong?.file)
  const isPodcast = usePlayer((s) => s.currentSong?.isPodcast)
  const { data } = usePodcastChapters(isPodcast ? file : null)
  const chapters = useMemo(() => sortChapters(data ?? []), [data])

  return useMemo(() => {
    if (!isPodcast || chapters.length === 0) return { prev: () => false, next: () => false }
    return {
      prev: () => {
        const target = prevChapterStart(chapters, engine.time())
        if (target === null) return false
        seekTo(target)
        return true
      },
      next: () => {
        const target = nextChapterStart(chapters, engine.time())
        if (target === null) return false
        seekTo(target)
        return true
      },
    }
  }, [isPodcast, chapters])
}

// Active chapter for lyrics + highlight: last chapter at or before the
// engine clock, polled like the auto-skip pump. Null when not a podcast
// or when the episode has no named chapters (callers fall back to the
// file flow). Name is guaranteed non-blank by backend validation.
export function useActiveChapter(file: string | null | undefined, isPodcast: boolean | undefined) {
  const { data } = usePodcastChapters(isPodcast ? file : null)
  const chapters = useMemo(() => sortChapters(data ?? []), [data])
  const [now, setNow] = useState(() => engine.time())
  useEffect(() => {
    const id = window.setInterval(() => setNow(engine.time()), 500)
    return () => window.clearInterval(id)
  }, [])
  return useMemo(() => {
    if (!isPodcast || !file) return null
    let active: (typeof chapters)[number] | null = null
    for (const c of chapters) {
      if (now >= c.startSecs && c.name.trim().length > 0) active = c
      else if (now < c.startSecs) break
    }
    return active
  }, [isPodcast, file, chapters, now])
}

// Auto-skip pump: while enabled and a podcast episode plays, jump into the
// next chapter once a chapter plays to its end time (markers without an
// end never skip). Mount once in the always-rendered player bar.
// The engine-file guard is load-bearing: the store and the element can
// disagree mid-switch (playSong loads audio before the server confirms,
// or the play call fails and the store stays stale) — without it, chapter
// timestamps from a previous episode would seek whatever song actually
// plays. Songs never have chapters; never seek them.
export function useChapterAutoSkip() {
  const enabled = usePlayer((s) => s.chapterSkip)
  const file = usePlayer((s) => s.currentSong?.file)
  const isPodcast = usePlayer((s) => s.currentSong?.isPodcast)
  const isPlaying = usePlayer((s) => s.isPlaying)
  const { data } = usePodcastChapters(isPodcast ? file : null)
  const stateRef = useRef({ enabled, isPodcast, isPlaying, chapters: data ?? [] })
  stateRef.current = { enabled, isPodcast, isPlaying, chapters: data ?? [] }

  useEffect(() => {
    const id = window.setInterval(() => {
      const s = stateRef.current
      if (!s.enabled || !s.isPodcast || !s.isPlaying || s.chapters.length === 0) return
      // The element must be rendering the store's episode: after a switch
      // to a song (or a failed play call) the store can still name the old
      // podcast while the element already plays the song.
      if (!file || engine.currentFile() !== file) return
      const target = autoSkipTarget(sortChapters(s.chapters), engine.time())
      if (target !== null) seekTo(target)
    }, 500)
    return () => window.clearInterval(id)
  }, [file])
}

// "Use current position" helper: fills a time field with the engine clock.
function NowButton({ onPick }: { onPick: (secs: number) => void }) {
  return (
    <button
      type="button"
      onClick={() => onPick(engine.time())}
      title="Use current playback position"
      className="text-xs font-medium text-orange-400 hover:text-orange-300 transition-colors cursor-pointer whitespace-nowrap"
    >
      Now {formatTime(engine.time())}
    </button>
  )
}

function TimestampForm({
  initial,
  submitLabel,
  onSubmit,
  onCancel,
}: {
  initial?: PodcastTimestamp
  submitLabel: string
  onSubmit: (input: { name: string; startSecs: number; endSecs: number | null }) => void
  onCancel?: () => void
}) {
  const [name, setName] = useState(initial?.name ?? '')
  const [start, setStart] = useState(initial ? formatChapterTime(initial.startSecs) : '')
  const [end, setEnd] = useState(
    initial?.endSecs !== null && initial?.endSecs !== undefined
      ? formatChapterTime(initial.endSecs)
      : '',
  )
  const [error, setError] = useState<string | null>(null)

  const save = () => {
    const startSecs = parseChapterInput(start)
    if (name.trim().length === 0) return setError('Give the chapter a name.')
    if (startSecs === null) return setError('Start needs a time like 1:30 or 90.')
    let endSecs: number | null = null
    if (end.trim().length > 0) {
      const parsed = parseChapterInput(end)
      if (parsed === null || parsed <= startSecs)
        return setError('End must be a time later than the start.')
      endSecs = parsed
    }
    setError(null)
    onSubmit({ name: name.trim(), startSecs, endSecs })
  }

  return (
    <div className="flex flex-col gap-3 rounded-xl bg-white/[0.03] border border-white/[0.07] p-3 sm:p-4">
      <TextField
        label="Chapter name"
        value={name}
        onChange={(e) => setName(e.target.value)}
        placeholder="Intro, sponsor, Q&A…"
        maxLength={255}
      />
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <TextField
            label="Starts at"
            value={start}
            onChange={(e) => setStart(e.target.value)}
            placeholder="1:30"
            inputMode="decimal"
          />
          <div className="mt-1 flex justify-end">
            <NowButton onPick={(s) => setStart(formatChapterTime(s))} />
          </div>
        </div>
        <div>
          <TextField
            label="Ends at (optional)"
            value={end}
            onChange={(e) => setEnd(e.target.value)}
            placeholder="Open-ended"
            inputMode="decimal"
          />
          <div className="mt-1 flex justify-end">
            <NowButton onPick={(s) => setEnd(formatChapterTime(s))} />
          </div>
        </div>
      </div>
      {error && <p className="text-xs text-red-400">{error}</p>}
      <div className="flex justify-end gap-2">
        {onCancel && <GhostButton onClick={onCancel}>Cancel</GhostButton>}
        <PrimaryButton onClick={save}>{submitLabel}</PrimaryButton>
      </div>
    </div>
  )
}

// Full CRUD for one episode's chapters, in the app's glass-modal style.
export function TimestampManagerModal({
  file,
  episodeTitle,
  onClose,
}: {
  file: string
  episodeTitle: string
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [editingId, setEditingId] = useState<number | null>(null)
  const [confirmDeleteId, setConfirmDeleteId] = useState<number | null>(null)
  const [formError, setFormError] = useState<string | null>(null)

  const { data, isLoading } = useQuery({
    queryKey: chapterKey(file),
    queryFn: () => api.podcastTimestamps(file),
  })
  const chapters = useMemo(() => sortChapters(data ?? []), [data])

  const refresh = () => void queryClient.invalidateQueries({ queryKey: chapterKey(file) })

  const create = useMutation({
    mutationFn: (input: { name: string; startSecs: number; endSecs: number | null }) =>
      api.createPodcastTimestamp(file, input),
    onSuccess: refresh,
    onError: (e) => setFormError(e instanceof Error ? e.message : 'Could not add the chapter.'),
  })
  const update = useMutation({
    mutationFn: (args: { id: number; input: { name: string; startSecs: number; endSecs: number | null } }) =>
      api.updatePodcastTimestamp(file, args.id, args.input),
    onSuccess: () => {
      setEditingId(null)
      refresh()
    },
  })
  const remove = useMutation({
    mutationFn: (id: number) => api.deletePodcastTimestamp(file, id),
    onSuccess: () => {
      setConfirmDeleteId(null)
      refresh()
    },
  })

  return (
    <Modal wide open onClose={onClose} title="Chapters">
      <p className="text-xs text-zinc-500 truncate -mt-2 mb-1">{episodeTitle}</p>
      <p className="text-xs text-zinc-600 mb-4">
        With Auto-skip on, playback starts at the first chapter and jumps to the next one at
        each end time.
      </p>
      {isLoading ? (
        <LoadingState label="Loading chapters…" />
      ) : chapters.length === 0 ? (
        <p className="text-sm text-zinc-500 text-center py-4">
          No chapters yet — mark the moments worth jumping back to.
        </p>
      ) : (
        <div className="flex flex-col gap-1 mb-5 max-h-64 overflow-y-auto pr-1">
          {chapters.map((c) => (
            <div key={c.id}>
              {editingId === c.id ? (
                <TimestampForm
                  initial={c}
                  submitLabel="Save"
                  onCancel={() => setEditingId(null)}
                  onSubmit={(input) => update.mutate({ id: c.id, input })}
                />
              ) : (
                <div className="grid grid-cols-[auto_minmax(0,1fr)_auto] gap-3 items-center p-2.5 px-3 rounded-xl hover:bg-white/5 transition-colors group">
                  <button
                    onClick={() => seekTo(c.startSecs)}
                    title={`Skip to ${formatChapterTime(c.startSecs)}`}
                    className="font-mono text-xs font-semibold text-orange-400 bg-orange-500/10 border border-orange-500/20 rounded-lg px-2 py-1 hover:bg-orange-500/20 transition-all cursor-pointer whitespace-nowrap"
                  >
                    {formatChapterTime(c.startSecs)}
                  </button>
                  <div className="min-w-0">
                    <div className="text-sm font-medium text-zinc-100 truncate">{c.name}</div>
                    <div className="text-xs text-zinc-500">
                      {c.endSecs !== null && c.endSecs !== undefined
                        ? `→ ${formatChapterTime(c.endSecs)}`
                        : 'open-ended'}
                    </div>
                  </div>
                  <div className="flex items-center gap-1 opacity-100 sm:opacity-0 sm:group-hover:opacity-100 transition-opacity">
                    <button
                      onClick={() => {
                        setConfirmDeleteId(null)
                        setEditingId(c.id)
                      }}
                      title="Edit chapter"
                      className="p-1.5 rounded-full text-zinc-500 hover:text-white hover:bg-white/10 transition-all cursor-pointer"
                    >
                      <Icon name="gear" className="w-4 h-4 pointer-events-none" />
                    </button>
                    {confirmDeleteId === c.id ? (
                      <button
                        onClick={() => remove.mutate(c.id)}
                        title="Confirm delete"
                        className="px-2 py-1 rounded-lg text-xs font-semibold text-white bg-red-500/80 hover:bg-red-500 transition-all cursor-pointer"
                      >
                        Sure?
                      </button>
                    ) : (
                      <button
                        onClick={() => setConfirmDeleteId(c.id)}
                        title="Delete chapter"
                        className="p-1.5 rounded-full text-zinc-500 hover:text-red-300 hover:bg-white/10 transition-all cursor-pointer"
                      >
                        <Icon name="x" className="w-4 h-4 pointer-events-none" />
                      </button>
                    )}
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      )}
      <div className="border-t border-white/10 pt-4">
        <h3 className="text-sm font-semibold text-zinc-300 mb-3">Add a chapter</h3>
        {formError && <p className="text-xs text-red-400 mb-2">{formError}</p>}
        <TimestampForm submitLabel="Add chapter" onSubmit={(input) => create.mutate(input)} />
      </div>
    </Modal>
  )
}

// Skip-to-chapter strip for player surfaces (queue sheet, now playing).
// Mobile: horizontal snap-scroll chips; sm+: vertical rows. The active
// chapter tracks the engine clock and stays scrolled into view.
export function ChapterSkip({ file }: { file: string }) {
  const { data } = usePodcastChapters(file)
  const chapters = useMemo(() => sortChapters(data ?? []), [data])
  const [position, setPosition] = useState(() => engine.time())
  const chapterSkip = usePlayer((s) => s.chapterSkip)
  const setChapterSkip = usePlayer((s) => s.setChapterSkip)
  const activeRef = useRef<HTMLButtonElement | null>(null)

  useEffect(() => {
    const id = window.setInterval(() => setPosition(engine.time()), 500)
    return () => window.clearInterval(id)
  }, [])

  const active = useMemo(() => activeChapterAt(chapters, position), [chapters, position])

  useEffect(() => {
    // jsdom (tests) has no scrollIntoView — guard the live-scroll hint.
    activeRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest', inline: 'nearest' })
  }, [active?.id])

  if (chapters.length === 0) return null

  return (
    <div className="flex flex-col gap-2 min-w-0">
      <div className="flex items-center gap-2 px-1">
        <Icon name="clock" className="w-4 h-4 text-orange-400" />
        <span className="text-xs font-semibold uppercase tracking-[0.15em] text-zinc-400">
          Chapters
        </span>
        <span className="text-xs text-zinc-600">{chapters.length}</span>
        <label
          className="ml-auto flex items-center gap-2 cursor-pointer"
          title="Start from the first chapter, and jump to the next chapter at each end time"
        >
          <span className="text-xs text-zinc-500">Auto-skip</span>
          <ToggleSwitch checked={chapterSkip} onChange={setChapterSkip} label="Auto-skip chapter segments" />
        </label>
      </div>
      <div className="flex sm:flex-col gap-2 overflow-x-auto sm:overflow-visible sm:max-h-64 sm:overflow-y-auto pb-1 sm:pb-0 -mx-1 px-1 snap-x">
        {chapters.map((c) => {
          const isActive = active?.id === c.id
          const isPast = !isActive && position >= c.startSecs
          return (
            <button
              key={c.id}
              ref={isActive ? activeRef : undefined}
              onClick={() => seekTo(c.startSecs)}
              title={`Skip to ${c.name} (${formatChapterTime(c.startSecs)})`}
              className={`snap-start flex-shrink-0 sm:flex-shrink min-w-[10rem] sm:min-w-0 grid grid-cols-[auto_minmax(0,1fr)] gap-2 items-center text-left p-2 px-2.5 rounded-xl border transition-all duration-300 cursor-pointer ${
                isActive
                  ? 'bg-orange-500/[0.08] border-orange-500/40 ring-1 ring-orange-500/30'
                  : 'bg-white/[0.03] border-white/[0.07] hover:bg-white/[0.07] hover:border-white/15'
              }`}
            >
              <span
                className={`font-mono text-xs font-semibold rounded-md px-1.5 py-0.5 whitespace-nowrap ${
                  isActive ? 'text-orange-300 bg-orange-500/15' : 'text-zinc-400 bg-white/5'
                }`}
              >
                {formatChapterTime(c.startSecs)}
              </span>
              <span className="min-w-0">
                <span
                  className={`block text-[13px] font-medium truncate ${
                    isActive ? 'text-orange-200' : isPast ? 'text-zinc-300' : 'text-zinc-400'
                  }`}
                >
                  {c.name}
                </span>
                {c.endSecs !== null && c.endSecs !== undefined && (
                  <span className="block text-[11px] text-zinc-600">→ {formatChapterTime(c.endSecs)}</span>
                )}
              </span>
            </button>
          )
        })}
      </div>
    </div>
  )
}
