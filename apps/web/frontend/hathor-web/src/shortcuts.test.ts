import { beforeEach, describe, expect, it, vi } from 'vitest'
import { handleShortcutKey, SHORTCUT_HELP, type ShortcutContext } from './utils/shortcuts'

function ctx(overrides: Partial<ShortcutContext> = {}): ShortcutContext & Record<string, ReturnType<typeof vi.fn>> {
  const base = {
    toggle: vi.fn(),
    seekBy: vi.fn(),
    nudgeVolume: vi.fn(),
    nextTrack: vi.fn(),
    prevTrack: vi.fn(),
    chapterNext: vi.fn(() => false),
    chapterPrev: vi.fn(() => false),
    shuffle: vi.fn(),
    repeat: vi.fn(),
    mute: vi.fn(),
    toggleHelp: vi.fn(),
  }
  return { ...base, ...overrides } as ShortcutContext & Record<string, ReturnType<typeof vi.fn>>
}

function key(key: string, target?: HTMLElement | null, code?: string): KeyboardEvent {
  const e = new KeyboardEvent('keydown', { key, bubbles: true })
  Object.defineProperty(e, 'code', { value: code ?? (key === ' ' ? 'Space' : key) })
  Object.defineProperty(e, 'target', { value: target ?? document.body })
  return e
}

beforeEach(() => {
  document.body.innerHTML = ''
})

describe('handleShortcutKey', () => {
  it('Space toggles and prevents scroll', () => {
    const c = ctx()
    const e = key(' ', document.body, 'Space')
    const prevented = vi.spyOn(e, 'preventDefault')
    expect(handleShortcutKey(e, c)).toBe(true)
    expect(c.toggle).toHaveBeenCalledOnce()
    expect(prevented).toHaveBeenCalledOnce()
  })

  it('arrows seek and nudge volume', () => {
    const c = ctx()
    expect(handleShortcutKey(key('ArrowRight'), c)).toBe(true)
    expect(c.seekBy).toHaveBeenCalledWith(10)
    expect(handleShortcutKey(key('ArrowLeft'), c)).toBe(true)
    expect(c.seekBy).toHaveBeenCalledWith(-10)
    expect(handleShortcutKey(key('ArrowUp'), c)).toBe(true)
    expect(c.nudgeVolume).toHaveBeenCalledWith(0.05)
    expect(handleShortcutKey(key('ArrowDown'), c)).toBe(true)
    expect(c.nudgeVolume).toHaveBeenCalledWith(-0.05)
  })

  it('n/p prefer chapters, falling back to tracks', () => {
    const jumped = ctx({ chapterNext: vi.fn(() => true), chapterPrev: vi.fn(() => true) })
    expect(handleShortcutKey(key('n'), jumped)).toBe(true)
    expect(jumped.nextTrack).not.toHaveBeenCalled()
    expect(handleShortcutKey(key('p'), jumped)).toBe(true)
    expect(jumped.prevTrack).not.toHaveBeenCalled()

    const plain = ctx()
    expect(handleShortcutKey(key('n'), plain)).toBe(true)
    expect(plain.nextTrack).toHaveBeenCalledOnce()
    expect(handleShortcutKey(key('p'), plain)).toBe(true)
    expect(plain.prevTrack).toHaveBeenCalledOnce()
  })

  it('[ and ] are chapter-only (no track fallback)', () => {
    const c = ctx()
    expect(handleShortcutKey(key(']'), c)).toBe(true)
    expect(c.chapterNext).toHaveBeenCalledOnce()
    expect(c.nextTrack).not.toHaveBeenCalled()
    expect(handleShortcutKey(key('['), c)).toBe(true)
    expect(c.chapterPrev).toHaveBeenCalledOnce()
    expect(c.prevTrack).not.toHaveBeenCalled()
  })

  it('m/s/r/? fire their actions', () => {
    const c = ctx()
    for (const [k, fn] of [['m', 'mute'], ['s', 'shuffle'], ['r', 'repeat'], ['?', 'toggleHelp']] as const) {
      expect(handleShortcutKey(key(k), c)).toBe(true)
      expect(c[fn]).toHaveBeenCalledOnce()
    }
  })

  it('ignores keys while typing', () => {
    const c = ctx()
    const input = document.createElement('input')
    document.body.appendChild(input)
    expect(handleShortcutKey(key('n', input), c)).toBe(false)
    expect(c.nextTrack).not.toHaveBeenCalled()
    expect(c.chapterNext).not.toHaveBeenCalled()
  })

  it('ignores keys when a modal dialog owns the keyboard', () => {
    const c = ctx()
    const dialog = document.createElement('div')
    dialog.setAttribute('role', 'dialog')
    document.body.appendChild(dialog)
    expect(handleShortcutKey(key('n'), c)).toBe(false)
    expect(c.nextTrack).not.toHaveBeenCalled()
  })

  it('returns false for unbound keys', () => {
    expect(handleShortcutKey(key('q'), ctx())).toBe(false)
  })

  it('help list documents every bound key', () => {
    const text = SHORTCUT_HELP.map((r) => `${r.keys.join('')}${r.description}`).join(' ')
    for (const k of ['Space', 'n', 'p', '[', ']', 'm', 's', 'r', '?']) {
      expect(text).toContain(k)
    }
  })
})
