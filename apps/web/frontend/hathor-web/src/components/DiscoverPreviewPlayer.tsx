import { useState, type CSSProperties } from 'react'
import type { DiscoverPreviewQueue } from '../hooks/useDiscoverPreviewQueue'
import { formatTime } from '../store/player'
import CoverArt from './ui/CoverArt'
import Icon from './ui/icons'

const STATUS_LABEL: Record<string, string> = {
  pending: 'Queued',
  resolving: 'Resolving',
  ready: 'Ready',
  playing: 'Previewing',
  failed: 'Unavailable',
}

export default function DiscoverPreviewPlayer({
  queue,
}: {
  queue: DiscoverPreviewQueue
}) {
  const [queueOpen, setQueueOpen] = useState(false)
  const current = queue.current
  if (!current) return null

  const percent =
    queue.durationSec > 0
      ? Math.min(100, Math.max(0, (queue.positionSec / queue.durationSec) * 100))
      : 0

  return (
    <div
      role="region"
      aria-label="Discover preview player"
      className="fixed right-0 z-10 border-t border-orange-500/20 bg-black/75 backdrop-blur-2xl shadow-[0_-10px_40px_rgba(0,0,0,0.45)]"
      style={{
        bottom: 'var(--controller-height, 112px)',
        left: 'var(--sidebar-width, 0px)',
      }}
    >
      <div className="relative mx-auto w-full max-w-5xl px-4 sm:px-8 py-2">
        {queueOpen && (
          <div
            id="discover-preview-queue"
            className="absolute bottom-full left-4 right-4 sm:left-8 sm:right-8 mb-2 max-h-64 overflow-y-auto rounded-2xl border border-white/10 bg-zinc-950/95 backdrop-blur-2xl shadow-2xl"
          >
            <div className="sticky top-0 flex items-center justify-between border-b border-white/5 bg-zinc-950/95 px-4 py-2 text-[10px] font-semibold uppercase tracking-[0.18em] text-zinc-500">
              <span>Preview queue</span>
              <span>
                {queue.index + 1} / {queue.tracks.length}
              </span>
            </div>
            <div className="p-1.5">
              {queue.tracks.map((track, i) => (
                <button
                  key={`${track.item.title}-${track.item.artist}`}
                  onClick={() => queue.jumpTo(i)}
                  className={`flex w-full items-center gap-3 rounded-xl px-3 py-2 text-left transition-colors cursor-pointer ${
                    i === queue.index
                      ? 'bg-orange-500/10 text-orange-300'
                      : 'text-zinc-300 hover:bg-white/5'
                  }`}
                >
                  <span className="w-4 text-right text-[10px] font-mono text-zinc-600">
                    {i === queue.index && queue.playing ? (
                      <span className="inline-flex h-2 w-2 rounded-full bg-orange-400" />
                    ) : (
                      i + 1
                    )}
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-xs font-medium">
                      {track.item.title}
                    </span>
                    <span className="block truncate text-[10px] text-zinc-500">
                      {track.item.artist}
                    </span>
                  </span>
                  <span
                    className={`shrink-0 text-[9px] font-semibold uppercase tracking-widest ${
                      track.status === 'failed'
                        ? 'text-red-400'
                        : track.status === 'resolving'
                          ? 'text-orange-400'
                          : 'text-zinc-600'
                    }`}
                  >
                    {STATUS_LABEL[track.status]}
                  </span>
                </button>
              ))}
            </div>
          </div>
        )}

        <div className="flex items-center gap-2.5">
          <CoverArt
            src={current.item.artworkUrl || null}
            alt={current.item.title}
            className="h-10 w-10 shrink-0 rounded-lg"
            iconClassName="h-5 w-5 text-zinc-600"
          />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2">
              <span className="truncate text-sm font-medium text-zinc-100">
                {current.item.title}
              </span>
              <span className="hidden shrink-0 text-[9px] font-semibold uppercase tracking-[0.18em] text-orange-400/80 sm:inline">
                Discover preview
              </span>
            </div>
            <div className="truncate text-xs text-zinc-500">
              {current.item.artist}
            </div>
          </div>

          <span className="hidden shrink-0 text-[10px] font-mono text-zinc-500 sm:inline">
            {queue.index + 1} / {queue.tracks.length}
          </span>

          <button
            onClick={queue.prev}
            title="Previous preview"
            aria-label="Previous preview"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-zinc-400 transition-colors hover:bg-white/5 hover:text-white cursor-pointer"
          >
            <Icon name="prev" className="h-4 w-4" />
          </button>
          <button
            onClick={queue.toggle}
            disabled={queue.buffering}
            aria-busy={queue.buffering}
            title={queue.playing ? 'Pause preview' : 'Play preview'}
            aria-label={queue.playing ? 'Pause preview' : 'Play preview'}
            className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-orange-500 text-white shadow-lg shadow-orange-500/30 transition-colors hover:bg-orange-400 disabled:cursor-default disabled:opacity-70 cursor-pointer"
          >
            {queue.buffering ? (
              <span className="spinner-btn" aria-hidden="true" />
            ) : (
              <Icon
                name={queue.playing ? 'pause' : 'play'}
                className="h-4 w-4"
              />
            )}
          </button>
          <button
            onClick={queue.next}
            title="Next preview"
            aria-label="Next preview"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-zinc-400 transition-colors hover:bg-white/5 hover:text-white cursor-pointer"
          >
            <Icon name="next" className="h-4 w-4" />
          </button>
          <button
            onClick={() => setQueueOpen((open) => !open)}
            title="Preview queue"
            aria-label="Preview queue"
            aria-expanded={queueOpen}
            aria-controls="discover-preview-queue"
            className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full transition-colors cursor-pointer ${
              queueOpen
                ? 'bg-orange-500/10 text-orange-400'
                : 'text-zinc-400 hover:bg-white/5 hover:text-white'
            }`}
          >
            <Icon name="queue" className="h-4 w-4" />
          </button>
          <button
            onClick={queue.close}
            title="Close preview"
            aria-label="Close preview"
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-zinc-500 transition-colors hover:bg-white/5 hover:text-white cursor-pointer"
          >
            <Icon name="x" className="h-4 w-4" />
          </button>
        </div>

        <div className="mt-1 flex items-center gap-2">
          <span className="w-9 shrink-0 text-right text-[10px] font-mono text-zinc-500">
            {formatTime(queue.positionSec)}
          </span>
          <input
            type="range"
            min="0"
            max={queue.durationSec || 0}
            step="0.1"
            value={Math.min(queue.positionSec, queue.durationSec || 0)}
            onChange={(event) => queue.seek(Number(event.target.value))}
            disabled={queue.durationSec <= 0}
            aria-label="Preview position"
            className="min-w-0 flex-1"
            style={{ '--range-percent': `${percent}%` } as CSSProperties}
          />
          <span className="w-9 shrink-0 text-[10px] font-mono text-zinc-500">
            {queue.durationSec > 0 ? formatTime(queue.durationSec) : '--:--'}
          </span>
        </div>

        {queue.notice && (
          <div
            role="status"
            aria-live="polite"
            className="mt-1 truncate text-[11px] text-zinc-400"
          >
            {queue.notice}
          </div>
        )}
      </div>
    </div>
  )
}
