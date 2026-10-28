import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'

const SOURCE_LABEL: Record<string, string> = {
  similar: 'Similar',
  cf: 'Similar listeners',
  graph: 'Related',
  tag: 'Genres',
  audio: 'Audio',
  popular: 'Popular',
  artist: 'Artists',
  chart: 'Charts',
  llm: 'LLM',
}

function pct(value: number) {
  return `${Math.round(Math.max(0, Math.min(1, value)) * 100)}%`
}

function Stat({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="rounded-xl bg-black/20 border border-white/5 px-3 py-2">
      <div className="text-[10px] uppercase tracking-widest text-zinc-500">{label}</div>
      <div className="text-lg font-semibold text-zinc-100 mt-0.5">{value}</div>
      <div className="text-[10px] text-zinc-600 truncate" title={hint}>
        {hint}
      </div>
    </div>
  )
}

// Local, per-user feedback quality. Hidden until there is something to show;
// this is the visible counterpart of the same aggregation the learned
// source/artist priors use.
export default function DiscoverInsightsCard() {
  const queryClient = useQueryClient()
  const [tuning, setTuning] = useState(false)
  const [tuneNotice, setTuneNotice] = useState<string | null>(null)
  const { data } = useQuery({
    queryKey: ['discover-insights'],
    queryFn: () => api.discoverInsights(),
    staleTime: 60_000,
  })

  const tune = () => {
    if (tuning) return
    setTuning(true)
    setTuneNotice(null)
    void api
      .tuneDiscover()
      .then((result) => {
        if (result.applied) {
          setTuneNotice(
            `Weights updated (${result.preset ?? 'tuned'}) — refreshing Discover…`,
          )
          void queryClient.invalidateQueries({ queryKey: ['discover'] })
        } else if (result.insufficientData) {
          setTuneNotice('Need more feedback before tuning weights.')
        } else {
          setTuneNotice('Current weights already perform best.')
        }
      })
      .catch(() => setTuneNotice('Tuning failed.'))
      .finally(() => setTuning(false))
  }

  if (!data || typeof data.downloads !== 'number') return null
  const activity =
    data.downloads +
    data.dismissals +
    data.previews +
    data.clicks +
    data.completedPlays +
    data.skippedPlays
  if (activity === 0) return null

  const outcomes = data.downloads + data.dismissals
  const sources = Array.isArray(data.sources) ? data.sources : []

  return (
    <div className="mt-4 rounded-2xl border border-white/5 bg-zinc-900/40 px-4 py-3">
      <div className="flex items-center justify-between gap-3">
        <span className="text-[10px] font-semibold uppercase tracking-widest text-zinc-500">
          Your signal
        </span>
        <div className="flex items-center gap-3">
          <span className="text-[10px] text-zinc-600">last {data.windowDays} days</span>
          <button
            onClick={tune}
            disabled={tuning}
            title="Compare weight presets using your feedback"
            className="text-[10px] uppercase tracking-widest text-zinc-500 hover:text-orange-400 disabled:text-zinc-700 transition-colors cursor-pointer disabled:cursor-default"
          >
            {tuning ? 'Tuning…' : 'Tune weights'}
          </button>
        </div>
      </div>
      <div className="mt-2 grid grid-cols-2 sm:grid-cols-4 gap-2">
        <Stat
          label="Conversion"
          value={pct(data.conversionRate)}
          hint={outcomes ? `${data.downloads} downloaded · ${data.dismissals} dismissed` : 'no outcomes yet'}
        />
        <Stat
          label="Playback"
          value={pct(data.playbackCompletionRate)}
          hint={`${data.completedPlays} completed · ${data.skippedPlays} skipped`}
        />
        <Stat
          label="Downloads"
          value={String(data.downloads)}
          hint={`${data.previews} previews · ${data.clicks} clicks`}
        />
        <Stat
          label="Artists"
          value={String(data.uniqueArtists)}
          hint="with recorded feedback"
        />
      </div>
      {sources.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {sources.map((source) => (
            <span
              key={source.source}
              title={`Learned prior ${pct(source.learnedPrior)}`}
              className="text-[10px] text-zinc-400 border border-white/10 bg-white/5 rounded-full px-2 py-1"
            >
              {SOURCE_LABEL[source.source] ?? source.source} · {source.downloads}/
              {source.downloads + source.dismissals}
            </span>
          ))}
        </div>
      )}
      {tuneNotice && (
        <div className="mt-2 text-[10px] text-zinc-400" role="status">
          {tuneNotice}
        </div>
      )}
    </div>
  )
}
