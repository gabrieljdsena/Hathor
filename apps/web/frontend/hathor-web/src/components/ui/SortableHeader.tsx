// Sortable grid header with ▲/▼ arrows (desktop table pattern).
// The grid column template comes from the parent so every table
// (songs, mix, artist, album, history) shares one header component.
export interface SortColumn<TKey extends string> {
  key: TKey
  label: string
  align?: 'left' | 'right'
  hideOnMobile?: boolean
}

export default function SortableHeader<TKey extends string>({
  columns,
  sort,
  dir,
  onSort,
  gridClass,
}: {
  columns: Array<SortColumn<TKey>>
  sort: TKey | null
  dir: 'asc' | 'desc'
  onSort: (key: TKey) => void
  gridClass: string
}) {
  return (
    <div
      className={`grid ${gridClass} gap-4 px-4 pb-3 mb-2 text-xs font-semibold tracking-wider text-zinc-500 uppercase border-b border-white/5`}
    >
      {columns.map((col) => (
        <button
          key={col.key}
          onClick={() => onSort(col.key)}
          className={`hover:text-zinc-300 transition-colors select-none flex items-center gap-1 cursor-pointer ${
            col.align === 'right' ? 'justify-end text-right' : ''
          }${col.hideOnMobile ? ' hidden sm:flex' : ''}`}
        >
          {col.label}{' '}
          <span className="text-orange-400 text-[10px]">{sort === col.key ? (dir === 'asc' ? '▲' : '▼') : ''}</span>
        </button>
      ))}
      <div />
    </div>
  )
}
