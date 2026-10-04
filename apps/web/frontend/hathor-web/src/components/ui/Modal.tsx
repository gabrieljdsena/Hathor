import { useEffect, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import Icon from './icons'

// Glass modal shell (desktop edit_song.html pattern): backdrop blur,
// ambient glow, Escape/backdrop close.
//
// Portaled to document.body: ancestors with backdrop-blur / transforms
// become containing blocks for `fixed` descendants, which used to trap the
// overlay off-center and under siblings. The portal keeps every modal
// viewport-centered and above everything (z-100 beats queue/sheets).
export default function Modal({
  open,
  onClose,
  title,
  children,
  wide = false,
}: {
  open: boolean
  onClose: () => void
  title: string
  children: ReactNode
  wide?: boolean
}) {
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  if (!open) return null
  const dialog = (
    <div
      className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 backdrop-blur-md p-4 overflow-y-auto"
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose()
      }}
      role="dialog"
      aria-modal="true"
      aria-label={title}
    >
      <div
        className={`bg-zinc-900/80 backdrop-blur-2xl rounded-2xl border border-white/10 shadow-2xl shadow-black/60 w-full p-6 relative my-auto ${
          wide ? 'max-w-2xl' : 'max-w-md'
        }`}
      >
        <div className="absolute -right-20 -top-20 w-48 h-48 bg-orange-500/10 rounded-full blur-[60px] pointer-events-none z-0" />
        <div className="flex items-center justify-between relative z-10 border-b border-white/10 pb-2 mb-4">
          <h2 className="text-2xl font-bold text-zinc-100">{title}</h2>
          <button
            onClick={onClose}
            title="Close"
            aria-label="Close"
            className="p-2 rounded-full text-zinc-500 hover:text-white hover:bg-white/10 transition-all focus:outline-none cursor-pointer"
          >
            <Icon name="x" className="w-5 h-5" />
          </button>
        </div>
        <div className="relative z-10">{children}</div>
      </div>
    </div>
  )
  return typeof document === 'undefined' ? dialog : createPortal(dialog, document.body)
}
