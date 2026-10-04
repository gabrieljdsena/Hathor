import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type DownloadJob, type VideoHit } from '../api/client'
import Icon from '../components/ui/icons'
import ConfirmModal from '../components/ui/ConfirmModal'
import Modal from '../components/ui/Modal'
import { formatDuration } from '../lyrics'

// Download view cloned from ui/views/download_songs.html:
// search card + Songs/Podcast toggle + trending + results + batch + jobs.
export default function Download() {
  const [query, setQuery] = useState('')
  const [isPodcast, setIsPodcast] = useState(false)
  const [results, setResults] = useState<VideoHit[] | null>(null)
  const [searching, setSearching] = useState(false)
  const [previewId, setPreviewId] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [batchBusy, setBatchBusy] = useState(false)
  // In-flight submit URLs: blocks double-click double-submits, which the
  // backend would otherwise persist as twin jobs ("Title (1).mp3").
  const [busyUrls, setBusyUrls] = useState<ReadonlySet<string>>(new Set())
  // Pending already-owned confirm (url/title/artist + owned filename).
  const [confirm, setConfirm] = useState<{
    url: string
    title: string
    artist: string | null
    file: string | null
  } | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const queryClient = useQueryClient()

  const { data: trending } = useQuery({ queryKey: ['trending'], queryFn: () => api.itunesTrending(4) })
  const { data: jobs } = useQuery({
    queryKey: ['download-jobs'],
    queryFn: () => api.downloadJobs(8),
    // Poll only while something is in flight — an idle Download page
    // issues zero requests instead of one every 2.5s forever.
    refetchInterval: (query) => {
      const list = (query.state.data ?? []) as DownloadJob[]
      const active = list.some(
        (j) => j.status !== 'completed' && j.status !== 'failed' && j.status !== 'cancelled',
      )
      return active ? 2500 : false
    },
  })

  useEffect(() => {
    if (!notice) return
    const t = window.setTimeout(() => setNotice(null), 4000)
    return () => window.clearTimeout(t)
  }, [notice])

  const search = async (q: string) => {
    const term = q.trim()
    if (!term) return
    setSearching(true)
    try {
      setResults(await api.youtubeSearch(term, 5))
    } catch (err) {
      setNotice(err instanceof Error ? err.message : 'Search failed.')
      setResults([])
    } finally {
      setSearching(false)
    }
  }

  const requestDownload = async (url: string, title: string) => {
    try {
      await api.submitDownload(url, title, null, isPodcast)
      setNotice(isPodcast ? `Podcast queued: ${title}` : `Download queued: ${title}`)
      void queryClient.invalidateQueries({ queryKey: ['download-jobs'] })
    } catch (err) {
      setNotice(err instanceof Error ? err.message : 'Download failed.')
    } finally {
      setBusyUrls((prev) => {
        const next = new Set(prev)
        next.delete(url)
        return next
      })
    }
  }

  // Ownership guard: titles matching the live library ask first instead of
  // silently producing "Title (1).mp3" twins.
  const download = async (url: string, title: string, artist: string | null) => {
    if (busyUrls.has(url)) return
    setBusyUrls((prev) => new Set(prev).add(url))
    const check = await api.checkDownload(title, artist).catch(() => null)
    if (check?.owned) {
      setConfirm({ url, title, artist, file: check.file })
      return
    }
    await requestDownload(url, title)
  }

  const cancelConfirm = () => {
    if (confirm) {
      const url = confirm.url
      setConfirm(null)
      setBusyUrls((prev) => {
        const next = new Set(prev)
        next.delete(url)
        return next
      })
    }
  }

  const isUrl = /^https?:\/\//i.test(query.trim())

  const batch = async (file: File | undefined) => {
    if (!file) return
    setBatchBusy(true)
    try {
      const res = await api.batchDownload(file, isPodcast)
      setNotice(`Starting import of ${res.accepted} songs`)
      void queryClient.invalidateQueries({ queryKey: ['download-jobs'] })
    } catch (err) {
      setNotice(err instanceof Error ? err.message : 'Batch import failed.')
    } finally {
      setBatchBusy(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  return (
    <div className="flex flex-col items-center justify-start w-full pt-12">
      <div className="text-center mb-8">
        <div className="inline-flex items-center justify-center w-16 h-16 rounded-2xl bg-gradient-to-br from-orange-400 to-orange-500 mb-5 shadow-lg shadow-orange-500/30">
          <Icon name="musicNote" className="w-8 h-8 text-white" />
        </div>
        <h1 className="text-3xl sm:text-4xl font-bold text-orange-400">Download Songs</h1>
        <p className="text-zinc-500 mt-2 text-sm sm:text-base tracking-wide">Search & download</p>
      </div>

      <div className="space-y-4 w-full max-w-2xl px-4 sm:px-0">
        <div className="relative group">
          <div className="relative flex items-center bg-white/[0.06] border border-white/[0.12] rounded-2xl overflow-hidden backdrop-blur-md transition-all duration-300 group-hover:border-white/[0.25] focus-within:border-orange-500 focus-within:ring-1 focus-within:ring-orange-500">
            <div className="pl-5 pr-3">
              <Icon name="search" className="w-5 h-5 text-gray-500 transition-colors duration-300 group-focus-within:text-orange-400" />
            </div>
            <input
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void search(query)
              }}
              placeholder="Search for songs, artists, or albums..."
              spellCheck={false}
              autoComplete="off"
              className="w-full py-4 pr-4 bg-transparent text-white placeholder-gray-500 text-base border-none tracking-wide focus:ring-0 focus:outline-none"
            />
            {query.length > 0 && (
              <button onClick={() => setQuery('')} className="pr-4 text-gray-500 hover:text-gray-300 transition-colors cursor-pointer" aria-label="Clear search">
                <Icon name="x" className="w-5 h-5" />
              </button>
            )}
          </div>
        </div>

        <div className="flex flex-col sm:flex-row gap-3 mt-5 w-full">
          <button
            onClick={() => void search(query)}
            disabled={searching}
            className="flex-1 rounded-2xl font-semibold text-white text-lg py-4 transition-all duration-300 active:scale-[0.98] hover:-translate-y-1 hover:shadow-xl hover:shadow-orange-500/30 bg-orange-500 hover:bg-orange-600 disabled:opacity-50 disabled:cursor-wait cursor-pointer"
          >
            <span className="flex items-center justify-center gap-3">
              {searching ? (
                <span className="spinner-btn" aria-hidden="true" />
              ) : (
                <Icon name="search" className="w-5 h-5" />
              )}
              <span>{searching ? 'Searching…' : 'Search Songs'}</span>
            </span>
          </button>
          {isUrl && (
            <button
              onClick={() => void download(query.trim(), query.trim(), null)}
              className="flex-1 rounded-2xl font-semibold text-orange-400 text-lg py-4 transition-all duration-300 active:scale-[0.98] hover:-translate-y-1 bg-white/[0.06] border border-orange-500/30 hover:border-orange-500 flex items-center justify-center gap-3 cursor-pointer whitespace-nowrap"
            >
              <Icon name="download" className="w-5 h-5" />
              <span>Download URL directly</span>
            </button>
          )}
          <button
            onClick={() => fileRef.current?.click()}
            disabled={batchBusy}
            className="sm:w-auto rounded-2xl font-semibold text-orange-400 text-lg py-4 px-6 transition-all duration-300 active:scale-[0.98] hover:-translate-y-1 bg-white/[0.06] border border-orange-500/30 hover:border-orange-500 flex items-center justify-center gap-3 disabled:opacity-50 disabled:cursor-wait cursor-pointer"
          >
            {batchBusy ? (
              <span className="spinner-btn" aria-hidden="true" />
            ) : (
              <Icon name="download" className="w-5 h-5" />
            )}
            <span className="whitespace-nowrap">{batchBusy ? 'Importing…' : 'Import .txt'}</span>
          </button>
          <input
            ref={fileRef}
            type="file"
            accept=".txt"
            className="hidden"
            onChange={(e) => void batch(e.target.files?.[0])}
          />
        </div>

        <div className="flex justify-center mt-4">
          <div className="inline-flex items-center gap-1 p-1 rounded-full bg-white/[0.05] border border-white/10">
            {(['Songs', 'Podcast / Audio'] as const).map((label) => {
              const pod = label !== 'Songs'
              const active = isPodcast === pod
              return (
                <button
                  key={label}
                  onClick={() => setIsPodcast(pod)}
                  className={`px-4 py-1.5 text-xs font-semibold rounded-full transition-all duration-200 cursor-pointer ${
                    active ? 'bg-orange-500 text-white' : 'text-zinc-400 hover:text-white'
                  }`}
                >
                  {label}
                </button>
              )
            })}
          </div>
        </div>

        <div className="flex flex-wrap gap-2 mt-5 justify-center items-center min-h-[28px]">
          <span className="text-xs text-gray-500 tracking-wider uppercase">Trending:</span>
          {(trending ?? []).map((t) => (
            <button
              key={t}
              onClick={() => {
                setQuery(t)
                void search(t)
              }}
              className="px-3 py-1 text-xs rounded-full bg-white/[0.05] border border-white/[0.08] text-gray-300 hover:bg-white/[0.12] hover:text-white transition-all duration-200 cursor-pointer whitespace-nowrap truncate max-w-[200px]"
              title={t}
            >
              {t.split(' (')[0].split(' - ')[0]}
            </button>
          ))}
        </div>

        {notice && (
          <div className="rounded-xl bg-orange-500/10 border border-orange-500/30 text-orange-200 text-sm px-4 py-3">
            {notice}
          </div>
        )}

        <div className="mt-8 w-full flex flex-col gap-3 pb-6">
          {(results ?? []).map((r) => (
            <ResultRow key={r.id} hit={r} busy={busyUrls.has(`https://www.youtube.com/watch?v=${r.id}`)} onDownload={() => void download(`https://www.youtube.com/watch?v=${r.id}`, r.title, r.uploader)} onPreview={() => setPreviewId(r.id)} />
          ))}
        </div>

        {(jobs ?? []).length > 0 && <JobsPanel jobs={jobs ?? []} />}
      </div>

      <Modal open={previewId !== null} onClose={() => setPreviewId(null)} title="Preview" wide>
        {previewId && (
          <div className="relative w-full" style={{ paddingBottom: '56.25%' }}>
            <iframe
              className="absolute top-0 left-0 w-full h-full rounded-lg"
              src={`https://www.youtube.com/embed/${previewId}?autoplay=1`}
              title="YouTube video player"
              allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
              allowFullScreen
            />
          </div>
        )}
      </Modal>

      <ConfirmModal
        open={confirm !== null}
        onCancel={cancelConfirm}
        onConfirm={() => {
          if (!confirm) return
          const { url, title } = confirm
          setConfirm(null)
          void requestDownload(url, title)
        }}
        title="Already in your library"
        message={
          confirm
            ? `"${confirm.title}" is already in your library${confirm.file ? ` (${confirm.file})` : ''}. Download it again anyway?`
            : ''
        }
        confirmLabel="Download anyway"
      />
    </div>
  )
}

function ResultRow({ hit, busy, onDownload, onPreview }: { hit: VideoHit; busy: boolean; onDownload: () => void; onPreview: () => void }) {
  return (
    <div className="flex items-center gap-4 bg-white/[0.04] border border-white/[0.08] rounded-xl p-3 hover:bg-white/[0.08] transition-all duration-300">
      <div className="relative w-24 h-16 flex-shrink-0 group rounded-lg overflow-hidden cursor-pointer shadow-md" onClick={onPreview}>
        {hit.thumbnail ? (
          <img src={hit.thumbnail} alt="" loading="lazy" className="w-full h-full object-cover" />
        ) : (
          <div className="w-full h-full bg-zinc-800" />
        )}
        <div className="absolute inset-0 bg-black/40 flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity">
          <Icon name="play" className="w-8 h-8 text-white" />
        </div>
      </div>
      <div className="flex-1 min-w-0">
        <h4 className="text-zinc-100 font-medium truncate text-sm sm:text-base">{hit.title}</h4>
        <p className="text-zinc-500 text-xs sm:text-sm truncate">
          {hit.uploader} • {formatDuration(hit.durationSec)}
        </p>
      </div>
      <button
        onClick={onDownload}
        disabled={busy}
        title={busy ? 'Queuing…' : 'Download'}
        className="flex items-center justify-center w-10 h-10 rounded-full bg-orange-500/10 text-orange-400 hover:bg-orange-500 hover:text-white disabled:opacity-50 transition-colors shadow-md cursor-pointer disabled:cursor-default flex-shrink-0"
      >
        <Icon name="download" className="w-5 h-5" />
      </button>
    </div>
  )
}

function JobsPanel({ jobs }: { jobs: import('../api/client').DownloadJob[] }) {
  const queryClient = useQueryClient()
  const act = (p: Promise<unknown>) =>
    void p
      .then(() => queryClient.invalidateQueries({ queryKey: ['download-jobs'] }))
      .catch(() => {})
  return (
    <div className="w-full flex flex-col gap-2 pb-24">
      <div className="text-xs uppercase tracking-wider text-zinc-500 font-semibold mt-2">Download Jobs</div>
      {jobs.map((job) => {
        const pct = Math.round((job.progress || 0) * 100)
        const active = job.status === 'queued' || job.status === 'downloading'
        return (
          <div key={job.qid} className="flex items-center gap-3 bg-white/[0.04] border border-white/[0.08] rounded-xl p-3 w-full">
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2">
                <span className="text-sm text-zinc-100 font-medium truncate">{job.title || job.url || 'Unknown'}</span>
                <StatusBadge status={job.status} />
              </div>
              {job.artist && <div className="text-xs text-zinc-500 truncate">{job.artist}</div>}
              {job.error && <div className="text-xs text-red-400/80 truncate">{job.error}</div>}
              {active && (
                <>
                  <div className="h-1.5 bg-white/10 rounded-full mt-2 overflow-hidden">
                    <div className="h-full rounded-full bg-orange-400 transition-all duration-300" style={{ width: `${pct}%` }} />
                  </div>
                  <div className="text-xs text-zinc-500 mt-1">{pct}%</div>
                </>
              )}
            </div>
            {job.status === 'failed' && (
              <button
                onClick={() => act(api.retryDownload(job.qid))}
                className="flex-shrink-0 px-3 py-1.5 rounded-lg bg-orange-500/15 text-orange-400 hover:bg-orange-500 hover:text-white transition-colors text-xs font-medium cursor-pointer"
              >
                Retry
              </button>
            )}
            {active && (
              <button
                onClick={() => act(api.cancelDownload(job.qid))}
                className="flex-shrink-0 px-3 py-1.5 rounded-lg bg-white/5 text-zinc-400 hover:bg-white/10 hover:text-white transition-colors text-xs font-medium cursor-pointer"
              >
                Cancel
              </button>
            )}
          </div>
        )
      })}
    </div>
  )
}

function StatusBadge({ status }: { status: string }) {
  const color =
    status === 'done'
      ? 'text-emerald-400'
      : status === 'failed'
        ? 'text-red-400'
        : status === 'downloading'
          ? 'text-orange-400'
          : 'text-zinc-400'
  return <span className={`text-xs flex-shrink-0 ${color}`}>{(status || 'queued').toUpperCase()}</span>
}
