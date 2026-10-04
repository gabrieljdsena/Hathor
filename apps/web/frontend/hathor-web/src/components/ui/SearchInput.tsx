import { useEffect, useState } from 'react'
import Icon from './icons'

// Expanding pill search with clear button (desktop all_songs.html pattern).
export default function SearchInput({
  value,
  onChange,
  placeholder = 'Search...',
  id,
}: {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  id?: string
}) {
  return (
    <div className="relative group">
      <Icon
        name="search"
        className="absolute left-3.5 top-1/2 -translate-y-1/2 w-4 h-4 text-zinc-500 group-focus-within:text-orange-400 transition-colors duration-300 pointer-events-none"
      />
      <input
        id={id}
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        spellCheck={false}
        className="w-48 sm:w-64 focus:w-72 lg:focus:w-80 transition-all duration-300 ease-[cubic-bezier(0.4,0,0.2,1)] bg-zinc-800/50 hover:bg-zinc-700/50 focus:bg-zinc-800/80 backdrop-blur-md border border-white/10 focus:border-orange-500/50 focus:ring-1 focus:ring-orange-500/50 rounded-full py-2.5 pl-10 pr-10 text-sm text-zinc-200 placeholder-zinc-500 focus:outline-none shadow-inner"
      />
      {value.length > 0 && (
        <button
          onClick={() => onChange('')}
          className="absolute right-3.5 top-1/2 -translate-y-1/2 text-gray-500 hover:text-gray-300 transition-colors duration-200 cursor-pointer"
          aria-label="Clear search"
        >
          <Icon name="x" className="w-4 h-4" />
        </button>
      )}
    </div>
  )
}

// Debounced mirror of a fast-changing value (search boxes).
export function useDebouncedValue<T>(value: T, delayMs = 200): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(value), delayMs)
    return () => window.clearTimeout(t)
  }, [value, delayMs])
  return debounced
}
