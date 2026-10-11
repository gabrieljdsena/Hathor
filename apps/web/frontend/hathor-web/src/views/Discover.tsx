import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type DiscoverItem, type DownloadJob } from '../api/client'
import CoverArt from '../components/ui/CoverArt'
import ConfirmModal from '../components/ui/ConfirmModal'
import Icon from '../components/ui/icons'
import Modal from '../components/ui/Modal'
import VideoPreview from '../components/ui/VideoPreview'
import SearchInput, { useDebouncedValue } from '../components/ui/SearchInput'
import { EmptyState, LoadingState } from '../components/ui/states'
import ViewHeader from '../components/ui/ViewHeader'

// Discover view: out-of-library recommendations (packages/contracts/discover.md).
// Cards carry iTunes artwork + source badge; download reuses the normal
// ingest pipeline (backend resolves "title artist audio" via YouTube search).
// While a card's download runs, its download button morphs into a progress
// pill fed by the shared download-jobs query (same polling rule as the
// Download view: quiet when nothing is in flight).
type SourceFilter = 'all' | 'artist' | 'chart' | 'llm'

const SOURCE_LABEL: Record<Exclude<SourceFilter, 'all'>, string> = {
  artist: 'From your artists',
  chart: 'Trending',
  llm: 'Picked for you',
}

const SOURCE_BADGE: Record<Exclude<SourceFilter, 'all'>, string> = {
  artist: 'text-orange-400 border-orange-500/30 bg-orange-500/10',
  chart: 'text-orange-400 border-orange-500/30 bg-orange-500/10',
  llm: 'text-violet-400 border-violet-500/30 bg-violet-500/10',
}

function matches(item: DiscoverItem, query: string) {
  const q = query.trim().toLowerCase()
  if (!q) return true
  return (
    item.title.toLowerCase().includes(q) ||
    item.artist.toLowerCase().includes(q) ||
    item.album.toLowerCase().includes(q)
  )
}

function DiscoverCard({
  item,
  pending,
  previewPending,
  progress,
  onDownload,
  onPreview,
}: {
  item: DiscoverItem
  pending: boolean
  previewPending: boolean
  // Active download progress 0..1, or null when idle (button shows).
  progress: number | null
  onDownload: () => void
  onPreview: () => void
}) {
  return (
    <div className="group relative rounded-2xl overflow-hidden bg-zinc-900/40 border border-white/5 hover:border-orange-500/40 transition-all duration-300">
      <div className="aspect-square w-full bg-zinc-800 overflow-hidden relative">
        <CoverArt
          src={item.artworkUrl || null}
          alt={item.title}
          className="w-full h-full"
          iconClassName="w-10 h-10 text-zinc-600"
          imgClassName="group-hover:scale-105 transition-transform duration-500"
        />
        <div className="absolute top-2 left-2">
          <span
            className={`text-[10px] font-semibold uppercase tracking-widest px-2 py-1 rounded-full border backdrop-blur-md ${SOURCE_BADGE[item.source] ?? SOURCE_BADGE.chart}`}
          >
            {SOURCE_LABEL[item.source] ?? item.source}
          </span>
        </div>
        <button
          onClick={onPreview}
          disabled={previewPending}
          title={previewPending ? 'Finding preview…' : `Preview ${item.title} on YouTube`}
          aria-label={`Preview ${item.title} by ${item.artist}`}
          className="absolute top-2 right-2 w-9 h-9 rounded-full bg-black/60 hover:bg-orange-500 disabled:bg-zinc-700 flex items-center justify-center text-white backdrop-blur-md transition-all duration-300 cursor-pointer disabled:cursor-default"
        >
          <Icon name="play" className="w-4 h-4 ml-0.5" />
        </button>
        {progress === null ? (
          <button
            onClick={onDownload}
            disabled={pending}
            title={pending ? 'Queuing…' : `Download ${item.title}`}
            aria-label={`Download ${item.title} by ${item.artist}`}
            className="absolute bottom-2 right-2 w-11 h-11 rounded-full bg-orange-500 hover:bg-orange-400 disabled:bg-zinc-700 flex items-center justify-center text-white shadow-lg shadow-orange-500/40 transition-all duration-300 cursor-pointer disabled:cursor-default"
          >
            <Icon name="download" className="w-5 h-5" />
          </button>
        ) : (
          <div
            role="progressbar"
            aria-valuenow={Math.round(progress * 100)}
            aria-valuemin={0}
            aria-valuemax={100}
            title={`Downloading ${item.title} — ${Math.round(progress * 100)}%`}
            className="absolute bottom-2 right-2 flex items-center gap-2 rounded-full bg-black/60 backdrop-blur-md pl-3 pr-2.5 py-2.5 cursor-default"
          >
            <div className="w-16 h-1.5 bg-white/10 rounded-full overflow-hidden">
              <div
                className="h-full rounded-full bg-orange-400 transition-all duration-300"
                style={{ width: `${Math.round(progress * 100)}%` }}
              />
            </div>
            <span className="text-[11px] font-mono text-zinc-200">{Math.round(progress * 100)}%</span>
          </div>
        )}
      </div>
      <div className="p-3 flex flex-col min-w-0">
        <div className="text-sm font-medium text-zinc-100 truncate group-hover:text-orange-400 transition-colors">
          {item.title}
        </div>
        <div className="text-xs text-zinc-500 truncate mt-0.5">{item.artist}</div>
        <div className="text-[11px] text-zinc-600 truncate mt-0.5">
          {[item.album, item.year].filter(Boolean).join(' · ') || ' '}
        </div>
      </div>
    </div>
  )
}

export default function Discover() {
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({ queryKey: ['discover'], queryFn: api.discover })
  const [search, setSearch] = useState('')
  const [source, setSource] = useState<SourceFilter>('all')
  const [notice, setNotice] = useState<string | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [pending, setPending] = useState<ReadonlySet<string>>(new Set())
  // Sync ref mirror of pending: state updates batch, so two clicks in one
  // tick would both submit without this guard.
  const pendingRef = useRef<Set<string>>(new Set())
  // Pending already-owned confirm.
  const [confirm, setConfirm] = useState<DiscoverItem | null>(null)
  const [previewing, setPreviewing] = useState<ReadonlySet<string>>(new Set())
  const [preview, setPreview] = useState<{ id: string; title: string; artist: string } | null>(null)
  const debounced = useDebouncedValue(search)

  // Job progress per card, matched by submitted title/artist (the backend
  // echoes both onto the job). Key includes the limit: the Download view
  // polls the same endpoint with limit 8, and sharing one key served each
  // view the other's cached limit. Polls only while something is in flight.
  const cardKey = (title: string, artist: string | null) =>
    `${title.trim().toLowerCase()} — ${(artist ?? '').trim().toLowerCase()}`
  const { data: jobs } = useQuery({
    queryKey: ['download-jobs', 50],
    queryFn: () => api.downloadJobs(50),
    refetchInterval: (query) => {
      const list = (query.state.data ?? []) as DownloadJob[]
      const active = Array.isArray(list) && list.some(
        (j) => j.status !== 'completed' && j.status !== 'failed' && j.status !== 'cancelled',
      )
      return active ? 2500 : false
    },
  })
  const jobByKey = new Map(
    (Array.isArray(jobs) ? jobs : []).map((j) => [cardKey(j.title ?? '', j.artist), j]),
  )
  // Submitted cards awaiting a terminal state (done/failed/cancelled).
  const trackedRef = useRef<Map<string, string>>(new Map())
  useEffect(() => {
    if (!Array.isArray(jobs)) return
    for (const [key, title] of [...trackedRef.current]) {
      const job = jobByKey.get(key)
      if (!job) continue
      if (job.status === 'done') {
        setNotice(`Downloaded: ${title}`)
        trackedRef.current.delete(key)
      } else if (job.status === 'failed' || job.status === 'cancelled') {
        setNotice(job.error || `Download failed: ${title}`)
        trackedRef.current.delete(key)
      }
    }
  }, [jobs])

  // Active progress 0..1 for a card, or null when its button shows.
  const progressFor = (item: DiscoverItem): number | null => {
    const job = jobByKey.get(cardKey(item.title, item.artist))
    if (!job) return null
    if (job.status === 'done' || job.status === 'failed' || job.status === 'cancelled') return null
    return typeof job.progress === 'number' && Number.isFinite(job.progress)
      ? Math.min(1, Math.max(0, job.progress))
      : 0
  }

  useEffect(() => {
    if (!notice) return
    const t = window.setTimeout(() => setNotice(null), 4000)
    return () => window.clearTimeout(t)
  }, [notice])

  const refresh = () => {
    if (refreshing) return
    setRefreshing(true)
    void api
      .refreshDiscover()
      .then((d) => {
        queryClient.setQueryData(['discover'], d)
      })
      .catch((err: unknown) => {
        setNotice(err instanceof Error ? err.message : 'Refresh failed.')
      })
      .finally(() => {
        setRefreshing(false)
      })
  }

  const previewItem = async (item: DiscoverItem) => {
    const key = `${item.title} — ${item.artist}`
    setPreviewing((prev) => new Set(prev).add(key))
    try {
      const hits = await api.youtubeSearch(`${item.title} ${item.artist}`, 1)
      const hit = hits[0]
      if (!hit) {
        setNotice(`No YouTube preview found for ${item.title}.`)
        return
      }
      setPreview({ id: hit.id, title: item.title, artist: item.artist })
    } catch (err) {
      setNotice(err instanceof Error ? err.message : 'Preview failed.')
    } finally {
      setPreviewing((prev) => {
        const next = new Set(prev)
        next.delete(key)
        return next
      })
    }
  }

  const requestDownload = async (item: DiscoverItem) => {
    const key = `${item.title} — ${item.artist}`
    try {
      await api.submitDownload(`${item.title} ${item.artist} audio`, item.title, item.artist, false)
      setNotice(`Download queued: ${item.title}`)
      trackedRef.current.set(cardKey(item.title, item.artist), item.title)
      void queryClient.invalidateQueries({ queryKey: ['download-jobs'] })
    } catch (err) {
      setNotice(err instanceof Error ? err.message : 'Download failed.')
    } finally {
      pendingRef.current.delete(key)
      setPending((prev) => {
        const next = new Set(prev)
        next.delete(key)
        return next
      })
    }
  }

  // Ownership guard: owned titles ask first instead of silently producing
  // "Title (1).mp3" twins.
  const download = async (item: DiscoverItem) => {
    const key = `${item.title} — ${item.artist}`
    if (pendingRef.current.has(key)) return
    pendingRef.current.add(key)
    setPending((prev) => new Set(prev).add(key))
    const check = await api.checkDownload(item.title, item.artist).catch(() => null)
    if (check?.owned) {
      setConfirm(item)
      return
    }
    await requestDownload(item)
  }

  const cancelConfirm = () => {
    if (!confirm) return
    const key = `${confirm.title} — ${confirm.artist}`
    setConfirm(null)
    pendingRef.current.delete(key)
    setPending((prev) => {
      const next = new Set(prev)
      next.delete(key)
      return next
    })
  }

  const items = (data?.items ?? []).filter(
    (i) => (source === 'all' || i.source === source) && matches(i, debounced),
  )

  const dateLabel = (date: string) => {
    try {
      return new Date(`${date}T00:00:00`).toLocaleDateString(undefined, {
        weekday: 'short',
        month: 'short',
        day: 'numeric',
      })
    } catch {
      return date
    }
  }

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        <div className="flex items-center gap-4 flex-wrap">
          <ViewHeader
            icon="compass"
            compact
            title="Discover"
            subtitle={
              isLoading
                ? 'Finding new music…'
                : `${items.length} suggestion${items.length === 1 ? '' : 's'}${data?.date ? ` · ${dateLabel(data.date)}` : ''}${
                    data && !data.cached ? ' · fresh' : ''
                  }`
            }
          />
          <div className="ml-auto flex items-center gap-2">
            {data && data.items.length > 0 && (
              <button
                onClick={refresh}
                disabled={refreshing}
                aria-busy={refreshing}
                title={refreshing ? 'Refreshing suggestions…' : 'Refresh suggestions'}
                className="inline-flex items-center gap-2 text-xs text-zinc-500 hover:text-orange-400 disabled:text-zinc-600 uppercase tracking-widest transition-colors cursor-pointer disabled:cursor-default"
              >
                {refreshing && <span className="spinner-btn" aria-hidden="true" />}
                {refreshing ? 'Refreshing…' : 'Refresh'}
              </button>
            )}
          </div>
        </div>
        <div className="flex items-center gap-3 mt-4 flex-wrap">
          <SearchInput value={search} onChange={setSearch} placeholder="Search suggestions..." id="discover_txt_search" />
          <div className="flex items-center gap-1.5">
            {(['all', 'artist', 'chart', 'llm'] as const).map((s) => (
              <button
                key={s}
                onClick={() => setSource(s)}
                className={`text-[11px] font-semibold uppercase tracking-widest px-3 py-1.5 rounded-full border transition-colors cursor-pointer ${
                  source === s
                    ? 'text-orange-400 border-orange-500/40 bg-orange-500/10'
                    : 'text-zinc-500 border-white/10 hover:text-zinc-300 hover:border-white/20'
                }`}
              >
                {s === 'all' ? 'All' : (SOURCE_LABEL[s] ?? s)}
              </button>
            ))}
          </div>
        </div>
        {notice && (
          <div className="mt-4 text-sm text-zinc-300 bg-zinc-900/60 border border-white/10 rounded-xl px-4 py-2.5">
            {notice}
          </div>
        )}
        {refreshing && (
          <div
            className="mt-4 flex items-center gap-2 text-sm text-zinc-400"
            role="status"
            aria-live="polite"
          >
            <span className="spinner" aria-hidden="true" />
            <span>Refreshing suggestions…</span>
          </div>
        )}
      </div>

      {isLoading ? (
        <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pt-6">
          <LoadingState label="Finding songs you haven't heard…" />
        </div>
      ) : items.length === 0 ? (
        <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pt-6">
          <EmptyState
            title="No suggestions right now"
            hint="Play and download more music — suggestions grow from your listening history."
          />
        </div>
      ) : (
        <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 py-6">
          <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-4">
            {items.map((item) => (
              <DiscoverCard
                key={`${item.title} — ${item.artist}`}
                item={item}
                pending={pending.has(`${item.title} — ${item.artist}`)}
                previewPending={previewing.has(`${item.title} — ${item.artist}`)}
                progress={progressFor(item)}
                onDownload={() => void download(item)}
                onPreview={() => void previewItem(item)}
              />
            ))}
          </div>
        </div>
      )}

      <Modal
        open={preview !== null}
        onClose={() => setPreview(null)}
        title={preview ? `Preview: ${preview.title}` : 'Preview'}
        wide
      >
        {preview && <VideoPreview id={preview.id} title={preview.title} />}
      </Modal>

      <ConfirmModal
        open={confirm !== null}
        onCancel={cancelConfirm}
        onConfirm={() => {
          if (!confirm) return
          const item = confirm
          setConfirm(null)
          void requestDownload(item)
        }}
        title="Already in your library"
        message={
          confirm
            ? `"${confirm.title}" by ${confirm.artist} is already in your library. Download it again anyway?`
            : ''
        }
        confirmLabel="Download anyway"
      />
    </div>
  )
}
