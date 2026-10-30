import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import { api, type DiscoverItem } from '../api/client'
import { usePlayer } from '../store/player'

const PREVIEW_LIMIT = 20
const PREFETCH = 2

export type DiscoverPreviewStatus =
  | 'pending'
  | 'resolving'
  | 'ready'
  | 'playing'
  | 'failed'

export interface DiscoverPreviewTrack {
  item: DiscoverItem
  status: DiscoverPreviewStatus
  videoId?: string
  audioUrl?: string
}

type ResolvedPreview = {
  videoId: string
  audioUrl: string
}

function previewKey(item: DiscoverItem): string {
  return `${item.title.trim().toLowerCase()}|${item.artist.trim().toLowerCase()}`
}

function isAutoplayBlocked(error: unknown): boolean {
  return (
    typeof error === 'object' &&
    error !== null &&
    'name' in error &&
    (error as { name?: string }).name === 'NotAllowedError'
  )
}

export interface DiscoverPreviewQueue {
  tracks: DiscoverPreviewTrack[]
  index: number
  current: DiscoverPreviewTrack | null
  playing: boolean
  buffering: boolean
  positionSec: number
  durationSec: number
  notice: string | null
  active: boolean
  audioRef: RefObject<HTMLAudioElement | null>
  start: (items: DiscoverItem[], startIndex?: number) => Promise<void>
  toggle: () => void
  next: () => void
  prev: () => void
  close: () => void
  jumpTo: (index: number) => void
  seek: (sec: number) => void
}

export function useDiscoverPreviewQueue(
  onStarted?: (item: DiscoverItem) => void,
): DiscoverPreviewQueue {
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const [tracks, setTracksState] = useState<DiscoverPreviewTrack[]>([])
  const tracksRef = useRef<DiscoverPreviewTrack[]>([])
  const [index, setIndexState] = useState(0)
  const indexRef = useRef(0)
  const [playing, setPlayingState] = useState(false)
  const playingRef = useRef(false)
  const [buffering, setBuffering] = useState(false)
  const [positionSec, setPositionSec] = useState(0)
  const [durationSec, setDurationSec] = useState(0)
  const [notice, setNotice] = useState<string | null>(null)
  const sessionRef = useRef(0)
  const playIdRef = useRef(0)
  const retryRef = useRef(false)
  const reportedRef = useRef(false)
  const cacheRef = useRef(new Map<string, Promise<ResolvedPreview | null>>())
  const onStartedRef = useRef(onStarted)
  onStartedRef.current = onStarted
  const mainPlaying = usePlayer((state) => state.isPlaying)

  const reportStarted = useCallback((item: DiscoverItem) => {
    if (reportedRef.current) return
    reportedRef.current = true
    onStartedRef.current?.(item)
  }, [])

  const markPlaying = useCallback(
    (item: DiscoverItem) => {
      const currentIndex = indexRef.current
      const active = tracksRef.current.map((track, i) =>
        i === currentIndex
          ? { ...track, status: 'playing' as const }
          : track,
      )
      tracksRef.current = active
      setTracksState(active)
      setBuffering(false)
      playingRef.current = true
      setPlayingState(true)
      setNotice((current) =>
        current === 'Press play to start the preview.' ||
        current === 'Main player is playing. Preview paused.'
          ? null
          : current,
      )
      reportStarted(item)
    },
    [reportStarted],
  )

  const resolve = useCallback(
    (item: DiscoverItem, force = false): Promise<ResolvedPreview | null> => {
      const key = previewKey(item)
      if (!force) {
        const cached = cacheRef.current.get(key)
        if (cached) return cached
      } else {
        cacheRef.current.delete(key)
      }

      const pending = (async () => {
        try {
          const hits = await api.youtubeSearch(
            `${item.title} ${item.artist}`.trim(),
            5,
          )
          for (const hit of hits) {
            try {
              const preview = await api.youtubePreview(hit.id, true)
              if (preview.audioUrl) {
                return { videoId: hit.id, audioUrl: preview.audioUrl }
              }
            } catch {
              // Try the next search hit.
            }
          }
        } catch {
          // Provider unavailable; the caller skips this item.
        }
        return null
      })()

      cacheRef.current.set(key, pending)
      void pending.then((resolved) => {
        if (!resolved && cacheRef.current.get(key) === pending) {
          cacheRef.current.delete(key)
        }
      })
      return pending
    },
    [],
  )

  const playIndexRef = useRef<(target: number, session: number) => Promise<void>>(
    async () => {},
  )

  const playIndex = useCallback(
    async (target: number, session: number) => {
      if (session !== sessionRef.current) return
      const playId = playIdRef.current + 1
      playIdRef.current = playId
      const isCurrent = () =>
        session === sessionRef.current && playId === playIdRef.current
      const audio = audioRef.current
      const queue = tracksRef.current
      if (!audio) return

      if (target < 0 || target >= queue.length) {
        audio.pause()
        playingRef.current = false
        setPlayingState(false)
        setBuffering(false)
        if (queue.length > 0) setNotice('End of preview queue.')
        return
      }

      indexRef.current = target
      setIndexState(target)
      retryRef.current = false
      reportedRef.current = false
      setBuffering(true)
      setPositionSec(0)
      setDurationSec(0)
      if (audio.getAttribute('src')) {
        audio.pause()
        audio.removeAttribute('src')
        audio.load()
      }

      const resolving = queue.map((track, i) =>
        i === target ? { ...track, status: 'resolving' as const } : track,
      )
      tracksRef.current = resolving
      setTracksState(resolving)
      const item = resolving[target].item

      const prefetchNext = () => {
        for (let offset = 1; offset <= PREFETCH; offset += 1) {
          const nextItem = tracksRef.current[target + offset]?.item
          if (nextItem) void resolve(nextItem)
        }
      }

      const markReady = (
        preview: ResolvedPreview,
        message = 'Press play to start the preview.',
      ) => {
        const ready = tracksRef.current.map((track, i) =>
          i === target
            ? { ...track, ...preview, status: 'ready' as const }
            : track,
        )
        tracksRef.current = ready
        setTracksState(ready)
        setBuffering(false)
        playingRef.current = false
        setPlayingState(false)
        setNotice(message)
        prefetchNext()
      }

      try {
        let resolved = await resolve(item)
        if (!isCurrent()) return
        if (!resolved) {
          const failed = tracksRef.current.map((track, i) =>
            i === target ? { ...track, status: 'failed' as const } : track,
          )
          tracksRef.current = failed
          setTracksState(failed)
          setBuffering(false)
          setNotice(`Preview unavailable: ${item.title} - skipping.`)
          void playIndexRef.current(target + 1, session)
          return
        }

        audio.src = resolved.audioUrl
        audio.currentTime = 0
        if (usePlayer.getState().isPlaying) {
          markReady(resolved, 'Main player is playing. Preview paused.')
          return
        }

        try {
          await audio.play()
        } catch (error) {
          if (!isCurrent()) return
          if (isAutoplayBlocked(error)) {
            markReady(resolved)
            return
          }
          if (usePlayer.getState().isPlaying) {
            markReady(resolved, 'Main player is playing. Preview paused.')
            return
          }
          resolved = await resolve(item, true)
          if (!isCurrent()) return
          if (!resolved) throw new Error('Preview unavailable')
          audio.src = resolved.audioUrl
          audio.currentTime = 0
          if (usePlayer.getState().isPlaying) {
            markReady(resolved, 'Main player is playing. Preview paused.')
            return
          }
          try {
            await audio.play()
          } catch (retryError) {
            if (!isCurrent()) return
            if (isAutoplayBlocked(retryError)) {
              markReady(resolved)
              return
            }
            if (usePlayer.getState().isPlaying) {
              markReady(resolved, 'Main player is playing. Preview paused.')
              return
            }
            throw retryError
          }
        }
        if (!isCurrent()) return
        if (usePlayer.getState().isPlaying) {
          audio.pause()
          markReady(resolved, 'Main player is playing. Preview paused.')
          return
        }

        const active = tracksRef.current.map((track, i) =>
          i === target
            ? { ...track, ...resolved, status: 'playing' as const }
            : track,
        )
        tracksRef.current = active
        setTracksState(active)
        setBuffering(false)
        playingRef.current = true
        setPlayingState(true)
        setNotice((current) =>
          current === 'Press play to start the preview.' ||
          current === 'Main player is playing. Preview paused.'
            ? null
            : current,
        )
        reportStarted(item)
        prefetchNext()
      } catch {
        if (!isCurrent()) return
        audio.pause()
        playingRef.current = false
        setPlayingState(false)
        setBuffering(false)
        const failed = tracksRef.current.map((track, i) =>
          i === target ? { ...track, status: 'failed' as const } : track,
        )
        tracksRef.current = failed
        setTracksState(failed)
        setNotice(`Preview failed: ${item.title} - skipping.`)
        void playIndexRef.current(target + 1, session)
      }
    },
    [resolve, reportStarted],
  )
  playIndexRef.current = playIndex

  useEffect(() => {
    const audio = audioRef.current ?? document.createElement('audio')
    audioRef.current = audio
    audio.preload = 'none'

    const onPlay = () => {
      playingRef.current = true
      setPlayingState(true)
    }
    const onPause = () => {
      playingRef.current = false
      setPlayingState(false)
    }
    const onTime = () => {
      setPositionSec(audio.currentTime || 0)
      setDurationSec(Number.isFinite(audio.duration) ? audio.duration : 0)
    }
    const onEnded = () => {
      void playIndexRef.current(indexRef.current + 1, sessionRef.current)
    }
    const onError = () => {
      if (!playingRef.current || !audio.getAttribute('src')) return
      if (usePlayer.getState().isPlaying) return
      const errorIndex = indexRef.current
      const item = tracksRef.current[errorIndex]?.item
      if (!item) return

      if (retryRef.current) {
        audio.pause()
        playingRef.current = false
        setPlayingState(false)
        setBuffering(false)
        setNotice(`Preview failed: ${item.title} - skipping.`)
        void playIndexRef.current(errorIndex + 1, sessionRef.current)
        return
      }

      retryRef.current = true
      playingRef.current = false
      setPlayingState(false)
      setBuffering(true)
      const session = sessionRef.current
      void resolve(item, true).then((resolved) => {
        if (session !== sessionRef.current || indexRef.current !== errorIndex) {
          return
        }
        if (!resolved) {
          setBuffering(false)
          setNotice(`Preview failed: ${item.title} - skipping.`)
          void playIndexRef.current(errorIndex + 1, session)
          return
        }
        audio.src = resolved.audioUrl
        audio.currentTime = 0
        void audio
          .play()
          .then(() => {
            if (
              session !== sessionRef.current ||
              indexRef.current !== errorIndex
            ) {
              return
            }
            if (usePlayer.getState().isPlaying) {
              audio.pause()
              return
            }
            markPlaying(item)
          })
          .catch((error: unknown) => {
            if (
              session !== sessionRef.current ||
              indexRef.current !== errorIndex
            ) {
              return
            }
            setBuffering(false)
            if (isAutoplayBlocked(error)) {
              const ready = tracksRef.current.map((track, i) =>
                i === errorIndex
                  ? { ...track, ...resolved, status: 'ready' as const }
                  : track,
              )
              tracksRef.current = ready
              setTracksState(ready)
              setNotice('Press play to start the preview.')
              return
            }
            setNotice(`Preview failed: ${item.title} - skipping.`)
            void playIndexRef.current(errorIndex + 1, session)
          })
      })
    }

    audio.addEventListener('play', onPlay)
    audio.addEventListener('pause', onPause)
    audio.addEventListener('timeupdate', onTime)
    audio.addEventListener('ended', onEnded)
    audio.addEventListener('error', onError)

    return () => {
      sessionRef.current += 1
      audio.removeEventListener('play', onPlay)
      audio.removeEventListener('pause', onPause)
      audio.removeEventListener('timeupdate', onTime)
      audio.removeEventListener('ended', onEnded)
      audio.removeEventListener('error', onError)
      if (audio.getAttribute('src')) {
        audio.pause()
        audio.removeAttribute('src')
        audio.load()
      }
    }
  }, [markPlaying, resolve])

  useEffect(() => {
    if (!mainPlaying) return
    const audio = audioRef.current
    if (audio && !audio.paused) audio.pause()
  }, [mainPlaying])

  useEffect(() => {
    if (!notice) return
    const timer = window.setTimeout(() => setNotice(null), 6000)
    return () => window.clearTimeout(timer)
  }, [notice])

  const start = useCallback(async (items: DiscoverItem[], startIndex = 0) => {
    const window = items.slice(startIndex, startIndex + PREVIEW_LIMIT)
    if (window.length === 0) return

    const main = usePlayer.getState()
    if (main.isPlaying) void main.toggle()

    const session = sessionRef.current + 1
    sessionRef.current = session
    const audio = audioRef.current
    if (audio?.getAttribute('src')) {
      audio.pause()
      audio.removeAttribute('src')
      audio.load()
    }

    const next = window.map((item) => ({
      item,
      status: 'pending' as const,
    }))
    tracksRef.current = next
    setTracksState(next)
    indexRef.current = 0
    setIndexState(0)
    playingRef.current = false
    reportedRef.current = false
    setPlayingState(false)
    setBuffering(true)
    setPositionSec(0)
    setDurationSec(0)
    setNotice(null)
    await playIndexRef.current(0, session)
  }, [])

  const toggle = useCallback(() => {
    const audio = audioRef.current
    if (!audio) return
    const toggleSession = sessionRef.current
    const toggleIndex = indexRef.current
    const item = tracksRef.current[toggleIndex]?.item
    if (!item || tracksRef.current[toggleIndex]?.status === 'resolving') return

    if (!audio.getAttribute('src')) {
      const main = usePlayer.getState()
      if (main.isPlaying) void main.toggle()
      void playIndexRef.current(indexRef.current, sessionRef.current)
      return
    }

    if (audio.paused) {
      const main = usePlayer.getState()
      if (main.isPlaying) void main.toggle()
      void audio
        .play()
        .then(() => {
          if (
            sessionRef.current !== toggleSession ||
            indexRef.current !== toggleIndex
          ) {
            return
          }
          if (usePlayer.getState().isPlaying) {
            audio.pause()
            return
          }
          markPlaying(item)
        })
        .catch((error: unknown) => {
          if (
            sessionRef.current !== toggleSession ||
            indexRef.current !== toggleIndex
          ) {
            return
          }
          if (isAutoplayBlocked(error)) {
            setNotice('Press play to start the preview.')
            return
          }
          void resolve(item, true).then((resolved) => {
            if (
              !resolved ||
              sessionRef.current !== toggleSession ||
              indexRef.current !== toggleIndex
            ) {
              return
            }
            audio.src = resolved.audioUrl
            audio.currentTime = 0
            void audio
              .play()
              .then(() => {
                if (
                  sessionRef.current !== toggleSession ||
                  indexRef.current !== toggleIndex
                ) {
                  return
                }
                if (usePlayer.getState().isPlaying) {
                  audio.pause()
                  return
                }
                markPlaying(item)
              })
              .catch(() => setNotice('Playback blocked.'))
          })
        })
    } else {
      audio.pause()
    }
  }, [markPlaying, resolve])

  const next = useCallback(() => {
    if (tracksRef.current.length === 0) return
    audioRef.current?.pause()
    void playIndexRef.current(indexRef.current + 1, sessionRef.current)
  }, [])

  const prev = useCallback(() => {
    if (tracksRef.current.length === 0) return
    audioRef.current?.pause()
    void playIndexRef.current(
      Math.max(0, indexRef.current - 1),
      sessionRef.current,
    )
  }, [])

  const close = useCallback(() => {
    sessionRef.current += 1
    const audio = audioRef.current
    if (audio?.getAttribute('src')) {
      audio.pause()
      audio.removeAttribute('src')
      audio.load()
    }
    tracksRef.current = []
    setTracksState([])
    indexRef.current = 0
    setIndexState(0)
    playingRef.current = false
    reportedRef.current = false
    setPlayingState(false)
    setBuffering(false)
    setPositionSec(0)
    setDurationSec(0)
    setNotice(null)
  }, [])

  const jumpTo = useCallback((target: number) => {
    if (target < 0 || target >= tracksRef.current.length) return
    audioRef.current?.pause()
    void playIndexRef.current(target, sessionRef.current)
  }, [])

  const seek = useCallback((sec: number) => {
    const audio = audioRef.current
    if (!audio || !Number.isFinite(sec)) return
    const max = Number.isFinite(audio.duration) ? audio.duration : sec
    audio.currentTime = Math.min(Math.max(0, sec), Math.max(0, max))
  }, [])

  return {
    tracks,
    index,
    current: tracks[index] ?? null,
    playing,
    buffering,
    positionSec,
    durationSec,
    notice,
    active: tracks.length > 0,
    audioRef,
    start,
    toggle,
    next,
    prev,
    close,
    jumpTo,
    seek,
  }
}
