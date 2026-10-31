import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { api } from '../../api/client'
import Icon from './icons'

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

function fmt(sec: number): string {
  const s = Math.max(0, Math.floor(sec || 0))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

// App-styled replacement for native <audio controls> (Chrome's blue accent
// and default cursor looked out of place in the dark/orange UI).
function AudioPreview({ url, onFailed }: { url: string; onFailed: () => void }) {
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [playing, setPlaying] = useState(false)
  const [current, setCurrent] = useState(0)
  const [duration, setDuration] = useState(0)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    setPlaying(false)
    setCurrent(0)
    setDuration(0)
    setFailed(false)
  }, [url])

  const toggle = () => {
    const el = audioRef.current
    if (!el) return
    if (el.paused) {
      void Promise.resolve(el.play()).catch(() => {
        setFailed(true)
        onFailed()
      })
    } else {
      el.pause()
    }
  }

  const seek = (value: number) => {
    const el = audioRef.current
    if (!el) return
    el.currentTime = value
    setCurrent(value)
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-white/10 bg-white/[0.04] p-3">
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={toggle}
          aria-label={playing ? 'Pause preview' : 'Play preview'}
          className="flex h-10 w-10 flex-shrink-0 cursor-pointer items-center justify-center rounded-full bg-orange-500 text-white shadow-lg shadow-orange-500/30 transition-colors hover:bg-orange-400 focus:outline-none focus-visible:ring-2 focus-visible:ring-orange-300"
        >
          <Icon name={playing ? 'pause' : 'play'} className={`h-4 w-4 ${playing ? '' : 'ml-0.5'}`} />
        </button>
        <div className="min-w-0 flex-1">
          <input
            type="range"
            min={0}
            max={duration || 0}
            step="0.1"
            value={Math.min(current, duration || 0)}
            onChange={(e) => seek(Number(e.target.value))}
            aria-label="Preview position"
            className="w-full cursor-pointer"
            style={
              {
                '--range-percent': `${duration ? (current / duration) * 100 : 0}%`,
              } as CSSProperties
            }
          />
          <div className="mt-1 flex justify-between font-mono text-[11px] text-zinc-500">
            <span>{fmt(current)}</span>
            <span>{duration ? fmt(duration) : '—:—'}</span>
          </div>
        </div>
      </div>
      {failed && (
        <p className="text-xs text-red-300">Audio preview couldn&apos;t be played. Use Watch on YouTube.</p>
      )}
      <audio
        ref={audioRef}
        src={url}
        preload="none"
        hidden
        onLoadedMetadata={(e) => {
          const d = e.currentTarget.duration
          setDuration(Number.isFinite(d) ? d : 0)
        }}
        onTimeUpdate={(e) => setCurrent(e.currentTarget.currentTime)}
        onPlay={() => setPlaying(true)}
        onPause={() => setPlaying(false)}
        onEnded={() => {
          setPlaying(false)
          setCurrent(0)
        }}
        onError={() => {
          setPlaying(false)
          setFailed(true)
          onFailed()
        }}
      />
    </div>
  )
}

type Mode =
  | { kind: 'loading' }
  | { kind: 'iframe' }
  | { kind: 'resolving-audio' }
  | { kind: 'audio'; url: string }
  | { kind: 'dead' }

export default function VideoPreview({
  id,
  title,
  onUnavailable,
}: {
  id: string
  title?: string
  // Called when neither the embed nor the direct-audio fallback works, so
  // callers with more search hits can try the next candidate.
  onUnavailable?: () => void
}) {
  const [mode, setMode] = useState<Mode>({ kind: 'loading' })
  const [audioUrl, setAudioUrl] = useState<string | null>(null)
  const frameRef = useRef<HTMLIFrameElement | null>(null)
  const alive = useRef(true)
  const audioRetried = useRef(false)
  const unavailableRef = useRef(onUnavailable)
  unavailableRef.current = onUnavailable

  const markDead = () => {
    setMode({ kind: 'dead' })
    unavailableRef.current?.()
  }

  // Probe on open/video change. Embeddable → iframe (player-hooked below);
  // otherwise straight to audio or dead.
  useEffect(() => {
    alive.current = true
    audioRetried.current = false
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
          markDead()
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
                    if (!alive.current) return
                    if (p.audioUrl) {
                      setAudioUrl(p.audioUrl)
                      setMode({ kind: 'audio', url: p.audioUrl })
                    } else {
                      markDead()
                    }
                  })
                  .catch(() => {
                    if (alive.current) markDead()
                  })
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

  // Expired/blocked direct URLs get one fresh manifest lookup before we
  // give up; the URLs are time-limited by design.
  const retryAudio = () => {
    if (audioRetried.current) {
      markDead()
      return
    }
    audioRetried.current = true
    api
      .youtubePreview(id, true)
      .then((p) => {
        if (!alive.current) return
        if (p.audioUrl) {
          setAudioUrl(p.audioUrl)
          setMode({ kind: 'audio', url: p.audioUrl })
        } else {
          markDead()
        }
      })
      .catch(() => {
        if (alive.current) markDead()
      })
  }

  if (mode.kind === 'loading' || mode.kind === 'resolving-audio') {
    return <p className="text-zinc-500 text-sm animate-pulse py-8 text-center">Finding preview…</p>
  }

  if (mode.kind === 'iframe') {
    return (
      <div className="relative w-full" style={{ paddingBottom: '56.25%' }}>
        <iframe
          ref={frameRef}
          className="absolute top-0 left-0 w-full h-full rounded-lg"
          src={`https://www.youtube.com/embed/${id}?autoplay=0&enablejsapi=1&origin=${encodeURIComponent(window.location.origin)}`}
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
      {mode.kind === 'audio' ? (
        <>
          <p className="text-xs text-zinc-500">
            This video can't be embedded here — playing its audio instead.
          </p>
          {/* No autoPlay: an auto-starting preview under paused library audio
              sounds exactly like "another song not in the queue started". */}
          <AudioPreview url={mode.url} onFailed={retryAudio} />
        </>
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
