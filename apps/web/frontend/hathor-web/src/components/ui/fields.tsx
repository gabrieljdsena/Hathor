import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode } from 'react'

// Form primitives (desktop edit_song.html / add_playlist.html inputs + buttons).
// Every modal and settings row uses these — never raw <input>/<button> styling.

interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string
}

export function TextField({ label, id, ...rest }: TextFieldProps) {
  const inputId = id ?? `field-${label.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
  return (
    <div>
      <label htmlFor={inputId} className="block text-sm font-semibold text-zinc-300">
        {label}
      </label>
      <input
        id={inputId}
        {...rest}
        className="mt-1 block w-full rounded-lg border border-white/[0.12] shadow-sm focus:border-orange-500 focus:ring-1 focus:ring-orange-500 sm:text-sm px-3 py-2 bg-white/[0.06] text-zinc-100 placeholder-zinc-500 outline-none transition-all duration-300 hover:border-white/25 focus:bg-white/[0.08]"
      />
    </div>
  )
}

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  children: ReactNode
  loading?: boolean
}

function Spinner() {
  return <span className="spinner-btn" aria-hidden="true" />
}

export function PrimaryButton({ children, loading = false, className = '', disabled, ...rest }: ButtonProps) {
  return (
    <button
      {...rest}
      disabled={disabled || loading}
      aria-busy={loading}
      className={`px-4 py-2 bg-orange-500 text-white font-medium rounded-lg hover:bg-orange-600 focus:outline-none focus:ring-2 focus:ring-orange-500 focus:ring-opacity-50 transition-all duration-300 hover:scale-105 active:scale-95 shadow-md shadow-orange-500/20 hover:shadow-lg hover:shadow-orange-500/40 disabled:opacity-50 disabled:hover:scale-100 disabled:cursor-wait cursor-pointer ${className} ${
        loading ? 'inline-flex items-center justify-center gap-2' : ''
      }`}
    >
      {loading && <Spinner />}
      {children}
    </button>
  )
}

export function GhostButton({ children, loading = false, className = '', disabled, ...rest }: ButtonProps) {
  return (
    <button
      {...rest}
      disabled={disabled || loading}
      aria-busy={loading}
      className={`px-4 py-2 bg-white/[0.06] text-zinc-300 font-medium rounded-lg border border-white/10 hover:bg-white/[0.12] hover:text-white focus:outline-none transition-all duration-300 hover:scale-105 active:scale-95 cursor-pointer disabled:opacity-50 disabled:hover:scale-100 disabled:cursor-wait ${className} ${
        loading ? 'inline-flex items-center justify-center gap-2' : ''
      }`}
    >
      {loading && <Spinner />}
      {children}
    </button>
  )
}
