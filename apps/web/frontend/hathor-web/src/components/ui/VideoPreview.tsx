import { useEffect, useRef, useState } from 'react'
import { api } from '../../api/client'

// Embed-first YouTube preview with a direct-audio fallback. Two detection
// layers: the backend probes oEmbed up front, and the YouTube IFrame Player
// API reports runtime embed errors (101/150/153 — e.g. videos that pass
// oEmbed yet refuse to play embedded). Either signal swaps the iframe for
// an <audio> element playing the resolved stream. When neither works, a
// watch link is shown — some videos (age-restricted, deleted, private)
// cannot preview at all.

interface YouTubePlayer {
  destroy: () => void
}

interface YouTubeNamespace {
  Player: new (
    el: HTMLIFrameElement,
    opts: { events?: { onError?: (e: { data: number }) => void } },
  ) => YouTubePlayer
}

declare global {
  interface Window {
    YT?: YouTubeNamespace
    onYouTubeIframeAPIReady?: () => void
  }
}

let apiLoad: Promise<YouTubeNamespace | null> | null = null

// Loads the IFrame Player API once per page (cached). Resolves null when
// unavailable (CSP, offline, tests) — callers keep the plain iframe.
function loadYouTubeApi(): Promise<YouTubeNamespace | null> {
  if (typeof window === 'undefined') return Promise.resolve(null)
  if (window.YT?.Player) return Promise.resolve(window.YT)
  if (apiLoad) {
    // A cached in-flight load may predate a test-injected window.YT.
    return window.YT?.Player ? Promise.resolve(window.YT) : apiLoad
  }
  apiLoad = new Promise((resolve) => {
    const timer = window.setTimeout(() => resolve(window.YT?.Player ? window.YT : null), 8000)
    window.onYouTubeIframeAPIReady = () => {
      window.clearTimeout(timer)
      resolve(window.YT ?? null)
    }
    const tag = document.createElement('script')
    tag.src = 'https://www.youtube.com/iframe_api'
    tag.onerror = () => {
      window.clearTimeout(timer)
      resolve(null)
    }
    document.head.appendChild(tag)
  })
  return apiLoad
}

type Mode =
  | { kind: 'loading' }
  | { kind: 'iframe' }
  | { kind: 'resolving-audio' }
  | { kind: 'audio'; url: string }
  | { kind: 'dead' }

export default function VideoPreview({ id, title }: { id: string; title?: string }) {
  const [mode, setMode] = useState<Mode>({ kind: 'loading' })
  const [audioUrl, setAudioUrl] = useState<string | null>(null)
  const frameRef = useRef<HTMLIFrameElement | null>(null)
  const alive = useRef(true)

  // Probe on open/video change. Embeddable → iframe (player-hooked below);
  // otherwise straight to audio or dead.
  useEffect(() => {
    alive.current = true
    setMode({ kind: 'loading' })
    setAudioUrl(null)
    api
      .youtubePreview(id)
      .then((p) => {
        if (!alive.current) return
        if (p.embeddable) {
          setMode({ kind: 'iframe' })
        } else if (p.audioUrl) {
          setAudioUrl(p.audioUrl)
          setMode({ kind: 'audio', url: p.audioUrl })
        } else {
          setMode({ kind: 'dead' })
        }
      })
      .catch(() => {
        // Probe failed (offline?): assume embeddable so the iframe — which
        // needs no backend round trip — still gets its chance.
        if (alive.current) setMode({ kind: 'iframe' })
      })
    return () => {
      alive.current = false
    }
  }, [id])

  // Hook the rendered iframe with the Player API: a runtime embed error
  // (oEmbed said yes, the player says no) drops into the audio fallback.
  // Plain iframe stays when the API is unreachable — identical to before.
  // Keyed per video while in iframe mode; audioUrl reads through a ref so
  // a late second error uses the resolved URL instead of refetching.
  const audioRef = useRef<string | null>(null)
  audioRef.current = audioUrl
  const hookKey = mode.kind === 'iframe' ? id : null
  useEffect(() => {
    if (hookKey === null) return
    const frame = frameRef.current
    if (!frame) return
    let player: YouTubePlayer | null = null
    let cancelled = false
    void loadYouTubeApi().then((ns) => {
      if (cancelled || !ns) return
      const el = frameRef.current
      if (!el) return
      try {
        player = new ns.Player(el, {
          events: {
            onError: () => {
              const known = audioRef.current
              if (known) {
                setMode({ kind: 'audio', url: known })
              } else {
                setMode({ kind: 'resolving-audio' })
                api
                  .youtubePreview(id, true)
                  .then((p) => {
                    if (p.audioUrl) {
                      setAudioUrl(p.audioUrl)
                      setMode({ kind: 'audio', url: p.audioUrl })
                    } else {
                      setMode({ kind: 'dead' })
                    }
                  })
                  .catch(() => setMode({ kind: 'dead' }))
              }
            },
          },
        })
      } catch {
        // Player construction failed — the plain iframe remains usable.
      }
    })
    return () => {
      cancelled = true
      try {
        player?.destroy()
      } catch {
        // destroy() takes the iframe with it; modal teardown covers it.
      }
    }
  }, [hookKey, id])

  if (mode.kind === 'loading' || mode.kind === 'resolving-audio') {
    return <p className="text-zinc-500 text-sm animate-pulse py-8 text-center">Finding preview…</p>
  }

  if (mode.kind === 'iframe') {
    return (
      <div className="relative w-full" style={{ paddingBottom: '56.25%' }}>
        <iframe
          ref={frameRef}
          className="absolute top-0 left-0 w-full h-full rounded-lg"
          src={`https://www.youtube.com/embed/${id}?autoplay=1&enablejsapi=1&origin=${encodeURIComponent(window.location.origin)}`}
          title={title ?? 'YouTube video player'}
          allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
          allowFullScreen
        />
      </div>
    )
  }

  const watchUrl = `https://www.youtube.com/watch?v=${id}`
  return (
    <div className="flex flex-col gap-3 py-2">
      <p className="text-xs text-zinc-500">
        This video can't be embedded here — playing audio directly instead.
      </p>
      {mode.kind === 'audio' ? (
        <audio className="w-full" controls autoPlay src={mode.url} preload="none" />
      ) : (
        <p className="text-sm text-zinc-400">No preview available for this video.</p>
      )}
      <a
        className="text-sm text-orange-400 hover:text-orange-300 hover:underline underline-offset-2 w-fit"
        href={watchUrl}
        target="_blank"
        rel="noreferrer"
      >
        Watch on YouTube ↗
      </a>
    </div>
  )
}
