import { useEffect, useRef, useState } from 'react'
import { api } from '../../api/client'
import Icon from './icons'

// Cover image with music-note fallback. List endpoints omit covers (payload
// would be megabytes of base64), so rows pass `file` and the art is fetched
// lazily when the row scrolls into view — the desktop lazy-cover observer
// (ui/index.html lazyCoverObserver + get_cover_art_base64) ported to React.
// Results are cached module-wide (desktop window._coverCache, capped).
const coverCache = new Map<string, string | null>()
const inflight = new Map<string, Promise<string | null>>()

// Drop a file's cached art (metadata edit / remove / delete / redownload):
// otherwise the next lazy read serves the pre-edit bytes forever.
export function evictCoverCache(file: string) {
  coverCache.delete(file)
  inflight.delete(file)
}

function cacheSet(file: string, src: string | null) {
  if (coverCache.size > 50) {
    const first = coverCache.keys().next().value
    if (first !== undefined) coverCache.delete(first)
  }
  coverCache.set(file, src)
}

function fetchCover(file: string, isPodcast: boolean): Promise<string | null> {
  const hit = inflight.get(file)
  if (hit) return hit
  const p = (isPodcast ? api.podcastDetails(file) : api.song(file, true))
    .then((song) => song.coverArt)
    .catch(() => null)
    .finally(() => {
      inflight.delete(file)
    })
  inflight.set(file, p)
  return p
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
  const [lazy, setLazy] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  const boxRef = useRef<HTMLDivElement | null>(null)

  // A fresh src (player-bar current song, post-edit refresh) wins immediately.
  useEffect(() => {
    if (src && file) cacheSet(file, src)
  }, [src, file])

  // New file → drop the previous lazy result.
  useEffect(() => {
    setLazy(null)
    setFailed(false)
  }, [file])

  useEffect(() => {
    if (src || lazy || !file) return
    if (coverCache.has(file)) {
      setLazy(coverCache.get(file) ?? null)
      return
    }
    const el = boxRef.current
    if (!el || typeof IntersectionObserver === 'undefined') {
      void fetchCover(file, isPodcast).then((cover) => {
        cacheSet(file, cover)
        setLazy(cover)
      })
      return
    }
    let timer: number | undefined
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            io.disconnect()
            // Debounce fast scrolls so a fling doesn't fan out requests.
            timer = window.setTimeout(() => {
              void fetchCover(file, isPodcast).then((cover) => {
                cacheSet(file, cover)
                setLazy(cover)
              })
            }, 150)
          }
        }
      },
      { rootMargin: '100px' },
    )
    io.observe(el)
    return () => {
      io.disconnect()
      if (timer !== undefined) window.clearTimeout(timer)
    }
  }, [src, lazy, file, isPodcast])

  const effective = failed ? null : (src ?? lazy)
  if (effective) {
    return (
      <img
        src={effective}
        alt={alt}
        className={`${className} object-cover opacity-0 transition-opacity duration-500 ${imgClassName}`}
        loading="lazy"
        onLoad={(e) => e.currentTarget.classList.remove('opacity-0')}
        onError={() => {
          // Broken data (e.g. stale cache after REMOVE) → fall back to the note.
          if (file) cacheSet(file, null)
          setLazy(null)
          setFailed(true)
        }}
      />
    )
  }
  return (
    <div ref={boxRef} className={`${className} bg-zinc-800 flex items-center justify-center overflow-hidden`}>
      <Icon name="musicNote" className={iconClassName} />
    </div>
  )
}
