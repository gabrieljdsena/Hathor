// Shared pager (queue sheet, history tabs): previous/next + numbered
// window with ellipsis. 1-based pages; renders nothing for a single page.
export default function Pagination({
  page,
  totalPages,
  onChange,
}: {
  page: number
  totalPages: number
  onChange: (page: number) => void
}) {
  if (totalPages <= 1) return null
  const current = Math.min(Math.max(1, page), totalPages)
  const nums = new Set<number>([1, totalPages, current - 1, current, current + 1])
  const ordered = [...nums].filter((n) => n >= 1 && n <= totalPages).sort((a, b) => a - b)
  const btn =
    'min-w-9 px-2 py-2 rounded-lg text-sm font-medium transition-colors cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed'
  return (
    <div className="flex items-center justify-center gap-1.5" role="navigation" aria-label="Pagination">
      <button
        aria-label="Previous page"
        disabled={current <= 1}
        onClick={() => onChange(current - 1)}
        className={`${btn} bg-white/5 border border-white/10 text-zinc-300 hover:bg-white/10 hover:text-white`}
      >
        ‹
      </button>
      {ordered.map((n, i) => (
        <span key={n} className="flex items-center gap-1.5">
          {i > 0 && n - ordered[i - 1] > 1 && <span className="text-zinc-600 select-none">…</span>}
          <button
            aria-label={`Page ${n}`}
            aria-current={n === current ? 'page' : undefined}
            onClick={() => onChange(n)}
            className={`${btn} ${
              n === current
                ? 'bg-orange-500 text-white'
                : 'bg-white/5 border border-white/10 text-zinc-300 hover:bg-white/10 hover:text-white'
            }`}
          >
            {n}
          </button>
        </span>
      ))}
      <button
        aria-label="Next page"
        disabled={current >= totalPages}
        onClick={() => onChange(current + 1)}
        className={`${btn} bg-white/5 border border-white/10 text-zinc-300 hover:bg-white/10 hover:text-white`}
      >
        ›
      </button>
    </div>
  )
}
