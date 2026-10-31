import { useEffect, useId, useRef, useState } from 'react'
import { claimMenu, releaseMenu, subscribeMenu } from './menuBus'

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
  const menuId = useId()
  const [open, setOpen] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)
  const activeLabel = columns.find((col) => col.key === sort)?.label ?? 'Sort'

  useEffect(
    () =>
      subscribeMenu((id) => {
        if (id !== null && id !== menuId) setOpen(false)
      }),
    [menuId],
  )
  useEffect(() => () => releaseMenu(menuId), [menuId])

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => {
      if (!boxRef.current?.contains(e.target as Node)) {
        setOpen(false)
        releaseMenu(menuId)
      }
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false)
        releaseMenu(menuId)
      }
    }
    document.addEventListener('click', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('click', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open, menuId])

  const close = () => {
    setOpen(false)
    releaseMenu(menuId)
  }

  const toggle = () => {
    setOpen((v) => {
      const next = !v
      if (next) claimMenu(menuId)
      else releaseMenu(menuId)
      return next
    })
  }

  const choose = (key: TKey) => {
    onSort(key)
    close()
  }

  return (
    <>
      <div ref={boxRef} className="relative pb-3 mb-2 border-b border-white/5 sm:hidden">
        <button
          type="button"
          onClick={toggle}
          aria-haspopup="menu"
          aria-expanded={open}
          aria-label={sort ? `Sort: ${activeLabel}, ${dir === 'asc' ? 'ascending' : 'descending'}` : 'Sort'}
          className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-zinc-500 hover:text-zinc-300 transition-colors cursor-pointer"
        >
          {sort ? `Sort: ${activeLabel}` : 'Sort'}
          <span className="text-orange-400 text-[10px]">{sort ? (dir === 'asc' ? '▲' : '▼') : '▾'}</span>
        </button>
        {open && (
          <div
            role="menu"
            aria-label="Sort options"
            className="absolute left-0 top-full z-30 w-44 overflow-hidden rounded-xl border border-white/10 bg-zinc-900 p-1.5 shadow-2xl"
          >
            {columns.map((col) => {
              const active = sort === col.key
              return (
                <button
                  key={col.key}
                  type="button"
                  role="menuitemradio"
                  aria-checked={active}
                  aria-label={
                    active
                      ? `${col.label}, ${dir === 'asc' ? 'ascending' : 'descending'}`
                      : col.label
                  }
                  onClick={() => choose(col.key)}
                  className={`flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm transition-colors cursor-pointer ${
                    active
                      ? 'bg-orange-500/10 text-orange-300'
                      : 'text-zinc-300 hover:bg-white/5 hover:text-white'
                  }`}
                >
                  {col.label}
                  <span className="text-orange-400 text-[10px]" aria-hidden="true">
                    {active ? (dir === 'asc' ? '▲' : '▼') : ''}
                  </span>
                </button>
              )
            })}
          </div>
        )}
      </div>
      <div
        className={`hidden sm:grid ${gridClass} gap-4 px-4 pb-3 mb-2 text-xs font-semibold tracking-wider text-zinc-500 uppercase border-b border-white/5`}
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
    </>
  )
}
