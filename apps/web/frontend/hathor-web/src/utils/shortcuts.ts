// Global keyboard shortcuts (desktop Now Playing & shortcuts parity).
// Transport keys are chapter-aware for podcasts: n/p fall through to the
// next/previous TRACK when no chapter applies, while [ ] are chapter-only
// (no-ops without chapters). Ignored while typing or when a modal owns
// the keyboard (it renders role="dialog"; Escape stays the modal's).

export interface ShortcutContext {
  toggle: () => void
  seekBy: (deltaSec: number) => void
  nudgeVolume: (delta: number) => void
  nextTrack: () => void
  prevTrack: () => void
  /** Chapter seek; true when one happened (caller skips track fallback). */
  chapterNext: () => boolean
  chapterPrev: () => boolean
  shuffle: () => void
  repeat: () => void
  mute: () => void
  toggleHelp: () => void
}

export interface ShortcutHelpRow {
  keys: string[]
  description: string
}

export const SHORTCUT_HELP: ShortcutHelpRow[] = [
  { keys: ['Space'], description: 'Play / pause' },
  { keys: ['←', '→'], description: 'Seek ± 10s' },
  { keys: ['↑', '↓'], description: 'Volume up / down' },
  { keys: ['n', 'p'], description: 'Next / previous (chapter-aware on podcasts)' },
  { keys: ['[', ']'], description: 'Previous / next chapter (podcasts with chapters)' },
  { keys: ['m'], description: 'Mute / unmute' },
  { keys: ['s'], description: 'Toggle shuffle' },
  { keys: ['r'], description: 'Toggle repeat' },
  { keys: ['?'], description: 'This shortcut list' },
]

type KeyEventLike = Pick<KeyboardEvent, 'key' | 'code' | 'target'> & {
  preventDefault: () => void
}

/** Returns true when the key was consumed as a shortcut. */
export function handleShortcutKey(e: KeyEventLike, ctx: ShortcutContext): boolean {
  const el = e.target as HTMLElement | null
  const typing =
    el instanceof HTMLInputElement ||
    el instanceof HTMLTextAreaElement ||
    (el !== null && (el as HTMLElement).isContentEditable === true)
  if (typing) return false
  // A modal dialog owns the keyboard (its own Escape/backdrop close);
  // global transport must not fire underneath it.
  if (
    typeof document !== 'undefined' &&
    document.querySelector('[role="dialog"]') !== null
  ) {
    return false
  }

  if (e.code === 'Space') {
    e.preventDefault()
    ctx.toggle()
    return true
  }
  switch (e.key) {
    case 'ArrowRight':
      ctx.seekBy(10)
      return true
    case 'ArrowLeft':
      ctx.seekBy(-10)
      return true
    case 'ArrowUp':
      e.preventDefault()
      ctx.nudgeVolume(0.05)
      return true
    case 'ArrowDown':
      e.preventDefault()
      ctx.nudgeVolume(-0.05)
      return true
    case 'n':
    case 'N':
      if (!ctx.chapterNext()) ctx.nextTrack()
      return true
    case 'p':
    case 'P':
      if (!ctx.chapterPrev()) ctx.prevTrack()
      return true
    case ']':
      ctx.chapterNext()
      return true
    case '[':
      ctx.chapterPrev()
      return true
    case 'm':
    case 'M':
      ctx.mute()
      return true
    case 's':
    case 'S':
      ctx.shuffle()
      return true
    case 'r':
    case 'R':
      ctx.repeat()
      return true
    case '?':
      ctx.toggleHelp()
      return true
    default:
      return false
  }
}
