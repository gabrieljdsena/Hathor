import { useEffect, useRef, useState } from 'react'
import { api } from '../../api/client'
import Icon from './icons'

// Cover image with music-note fallback. Art is served as bytes from
// GET .../{file}/cover (ETag/browser-cached), so NO base64 ever sits in
// JS state — the previous module-wide base64 Map (30 entries) plus one
// lazy string per row was tens of MB of retained heap. Rows pass `file`
// and the <img> mounts only when the row scrolls near (same observer +
// debounce as before); the browser HTTP cache dedupes repeat views.
// `src` (data: URL) still wins when provided: edit-modal previews and the
// current-song payload that already carries art.
const versions = new Map<string, number>()

// Bump a file's cover version (metadata edit / remove / delete /
// redownload): rows re-render on query invalidation and read the new
// version, busting the browser cache for that file only.
export function evictCoverCache(file: string) {
  versions.set(file, (versions.get(file) ?? 0) + 1)
}

function versionOf(file: string | undefined): string | null {
  if (!file) return null
  const v = versions.get(file) ?? 0
  return v === 0 ? null : String(v)
}

export default function CoverArt({
  src,
  file,
  isPodcast = false,
  alt = '',
  className = 'w-10 h-10 rounded-lg',
  iconClassName = 'w-6 h-6 text-zinc-600',
  imgClassName = '',
}: {
  src: string | null | undefined
  file?: string
  isPodcast?: boolean
  alt?: string
  className?: string
  iconClassName?: string
  imgClassName?: string
}) {
  const [near, setNear] = useState(false)
  const [failed, setFailed] = useState(false)
  const boxRef = useRef<HTMLDivElement | null>(null)

  const url = !src && file ? api.coverUrl(file, versionOf(file), isPodcast) : null

  // New target → drop previous failure/visibility state.
  useEffect(() => {
    setNear(false)
    setFailed(false)
  }, [url])

  useEffect(() => {
    if (src || !file || near) return
    const el = boxRef.current
    if (!el || typeof IntersectionObserver === 'undefined') {
      setNear(true)
      return
    }
    let timer: number | undefined
    // NOTE: deliberately no fallback force-load (an old 2500ms timer once
    // loaded EVERY offscreen row's cover — the 2.3GB blowup). Rows stay on
    // the note fallback until scrolled near; rootMargin preloads ahead.
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            io.disconnect()
            // Debounce fast scrolls so a fling doesn't fan out requests.
            timer = window.setTimeout(() => setNear(true), 150)
          }
        }
      },
      // #main (not the viewport) scrolls: a generous margin keeps art
      // loading just before rows scroll into view.
      { rootMargin: '400px' },
    )
    io.observe(el)
    return () => {
      io.disconnect()
      if (timer !== undefined) window.clearTimeout(timer)
    }
  }, [src, file, near])

  const effective = failed ? null : (src ?? (near ? url : null))
  if (effective) {
    return (
      <img
        src={effective}
        alt={alt}
        className={`${className} object-cover opacity-0 transition-opacity duration-500 ${imgClassName}`}
        loading="lazy"
        onLoad={(e) => e.currentTarget.classList.remove('opacity-0')}
        onError={() => setFailed(true)}
      />
    )
  }
  return (
    <div ref={boxRef} className={`${className} bg-zinc-800 flex items-center justify-center overflow-hidden`}>
      <Icon name="musicNote" className={iconClassName} />
    </div>
  )
}
