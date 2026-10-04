// Async view states: spinner block + empty block. Every list view uses these
// instead of inline animate-pulse / empty divs.

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="py-16 text-center text-zinc-500 text-sm animate-pulse" role="status">
      <span className="spinner" aria-hidden="true" />
      <span className="block mt-2">{label}</span>
    </div>
  )
}

export function EmptyState({ title, hint }: { title: string; hint?: string }) {
  return (
    <div className="py-16 flex flex-col items-center justify-center text-center opacity-70">
      <p className="text-zinc-400 font-medium text-lg">{title}</p>
      {hint && <p className="text-sm text-zinc-500 mt-2 max-w-md">{hint}</p>}
    </div>
  )
}
