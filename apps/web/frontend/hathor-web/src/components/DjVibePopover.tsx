import { useEffect, useRef, useState } from 'react'
import { useAiDj } from '../store/aiDj'
import { useDjVibeRecorder } from '../hooks/useDjVibeRecorder'
import { useStartDjSet } from '../hooks/useStartDjSet'
import Icon from './ui/icons'
import { GhostButton, PrimaryButton } from './ui/fields'

export default function DjVibePopover({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [brief, setBrief] = useState('')
  const inputRef = useRef<HTMLTextAreaElement>(null)
  const { start, error } = useStartDjSet()
  const loading = useAiDj((s) => s.loading)
  const recorder = useDjVibeRecorder((text) => {
    setBrief((current) => (current ? `${current} ${text}` : text))
  })

  useEffect(() => {
    if (!open) return
    const timer = window.setTimeout(() => inputRef.current?.focus(), 40)
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('keydown', onKey)
    }
  }, [open, onClose])

  if (!open) return null

  const submit = async () => {
    if (await start(brief)) {
      setBrief('')
      onClose()
    }
  }

  return (
    <div
      className="fixed right-4 z-[90] w-[min(430px,calc(100vw-2rem))] rounded-3xl border border-white/10 bg-zinc-950/95 p-4 shadow-2xl shadow-black/60 backdrop-blur-2xl"
      style={{ bottom: 'calc(var(--controller-height, 112px) + 1rem)' }}
    >
      <div className="flex items-center justify-between gap-3">
        <div>
          <div className="text-xs uppercase tracking-[0.25em] text-orange-400">Vohithe</div>
          <div className="text-sm font-semibold text-zinc-100">Tell her the vibe</div>
        </div>
        <button
          onClick={onClose}
          title="Close"
          className="rounded-full p-1.5 text-zinc-500 transition-colors hover:bg-white/10 hover:text-white cursor-pointer"
        >
          <Icon name="x" className="h-4 w-4" />
        </button>
      </div>
      <div className="mt-3 rounded-2xl border border-white/10 bg-white/[0.04] p-2 focus-within:border-orange-500/50">
        <textarea
          ref={inputRef}
          value={brief}
          onChange={(event) => setBrief(event.target.value)}
          rows={2}
          placeholder="warm evening indie, no sad songs…"
          className="w-full resize-none bg-transparent px-2 py-1 text-sm text-zinc-100 placeholder-zinc-500 outline-none"
        />
        <div className="flex items-center justify-between gap-2 px-1 pb-1">
          <button
            onClick={() => void recorder.start()}
            title={recorder.recording ? 'Stop listening' : 'Speak the vibe'}
            className={`flex items-center gap-1.5 rounded-full px-2.5 py-1.5 text-xs font-medium transition-colors cursor-pointer ${
              recorder.recording
                ? 'bg-red-500/20 text-red-200'
                : 'bg-white/[0.06] text-zinc-300 hover:bg-white/[0.12]'
            }`}
          >
            <Icon name="mic" className="h-3.5 w-3.5" />
            {recorder.recording ? 'Listening…' : 'Speak'}
          </button>
          <span className="text-[10px] text-zinc-600">Blank = Vohithe chooses for you</span>
        </div>
      </div>
      {recorder.error && <p className="mt-2 text-xs text-red-300">{recorder.error}</p>}
      {error && <p className="mt-2 text-xs text-red-300">{error}</p>}
      <div className="mt-3 flex items-center justify-end gap-2">
        <GhostButton onClick={onClose}>Cancel</GhostButton>
        <PrimaryButton onClick={() => void submit()} loading={loading}>
          Start set
        </PrimaryButton>
      </div>
    </div>
  )
}
