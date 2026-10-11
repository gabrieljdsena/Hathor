import { SHORTCUT_HELP } from '../../utils/shortcuts'
import Modal from './Modal'

// "?" overlay: every global shortcut in one place (discoverability for the
// headless-style keyboard surface). Purely presentational.
export default function ShortcutHelp({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Modal open={open} onClose={onClose} title="Keyboard shortcuts">
      <div className="flex flex-col gap-1.5">
        {SHORTCUT_HELP.map((row) => (
          <div key={row.description} className="flex items-center justify-between gap-4 py-1">
            <span className="text-sm text-zinc-300">{row.description}</span>
            <span className="flex gap-1 flex-shrink-0">
              {row.keys.map((k) => (
                <kbd
                  key={k}
                  className="min-w-7 text-center px-2 py-1 rounded-lg bg-white/5 border border-white/10 text-xs font-mono text-zinc-200"
                >
                  {k}
                </kbd>
              ))}
            </span>
          </div>
        ))}
        <p className="text-xs text-zinc-500 mt-3">
          Shortcuts pause while typing or when a dialog is open. On podcasts with chapters, track
          keys jump chapters first.
        </p>
      </div>
    </Modal>
  )
}
