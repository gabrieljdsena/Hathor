import { useEffect, useRef, type CSSProperties, type MouseEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { clampSeekTarget, engine } from '../audio/engine'
import { formatTime, usePlayer } from '../store/player'
import { ControlButton, PlayPauseButton } from './ui/buttons'
import CoverArt from './ui/CoverArt'
import Icon from './ui/icons'
import { useChapterAutoSkip, useChapterJump } from './ui/PodcastTimestamps'
import SongMenu, { type SongMenuHandle } from './ui/SongMenu'

// Self-ticking progress row (engine media time; display-only).
// One instance per layout (mobile stacked / desktop column).
function ProgressSlider({ idPrefix }: { idPrefix: string }) {
  const sliderRef = useRef<HTMLInputElement>(null)
  const curRef = useRef<HTMLSpanElement>(null)
  const totRef = useRef<HTMLSpanElement>(null)

  // Effective duration: the element's own once metadata loads (ground
  // truth for what can play), the song's stored duration before that so a
  // fresh long episode doesn't scrub against a stale/default maximum.
  const effDuration = () => {
    const d = engine.duration()
    if (d > 0) return d
    const meta = usePlayer.getState().currentSong?.duration ?? 0
    return meta > 0 ? meta : 0
  }

  useEffect(() => {
    const id = setInterval(() => {
      try {
        const slider = sliderRef.current
        if (!slider) return
        const dur = effDuration()
        const pos = engine.time()
        if (dur > 0) slider.max = String(dur)
        if (document.activeElement !== slider) slider.value = String(pos)
        if (curRef.current) curRef.current.textContent = formatTime(pos)
        if (totRef.current) totRef.current.textContent = formatTime(dur)
        const max = Number.parseFloat(slider.max) || 100
        const val = Number.parseFloat(slider.value) || 0
        slider.style.setProperty('--range-percent', `${(val / max) * 100}%`)
      } catch {
        // A throwing element read (unloaded mid-seek) must never kill the ticker.
      }
    }, 500)
    return () => clearInterval(id)
  }, [])

  const commitSeek = () => {
    const slider = sliderRef.current
    if (!slider) return
    // Same clamp as the engine's live scrub: server and element agree, so
    // no drift snap yanks playback back afterwards.
    const raw = Number.parseFloat(slider.value) || 0
    void api.seek(clampSeekTarget(raw, effDuration())).catch(() => {})
  }

  return (
    <>
      <span ref={curRef} className="text-xs text-zinc-500 font-medium font-mono">
        0:00
      </span>
      <input
        type="range"
        id={`${idPrefix}-progressSlider`}
        ref={sliderRef}
        min="0"
        step="0.1"
        defaultValue="0"
        onChange={(e) => {
          // Live local scrub only (cancels any fade); the server estimate
          // follows once on release so a drag doesn't fan out seeks.
          engine.seek(Number.parseFloat(e.target.value))
        }}
        onPointerUp={commitSeek}
        onKeyUp={commitSeek}
        className="w-full cursor-pointer outline-none border-none shadow-none focus:outline-none focus:ring-0"
      />
      <span ref={totRef} className="text-xs text-zinc-500 font-medium font-mono">
        0:00
      </span>
    </>
  )
}

// Shared volume slider (mobile + desktop): instant local gain, debounced
// server confirm so a drag sends one request, not dozens.
export function VolumeSlider({ volume, className, id }: { volume: number; className: string; id?: string }) {
  const timer = useRef<number | undefined>(undefined)
  useEffect(
    () => () => {
      if (timer.current !== undefined) window.clearTimeout(timer.current)
    },
    [],
  )
  return (
    <input
      type="range"
      id={id}
      aria-label="Volume"
      min="0"
      max="1"
      step="0.01"
      value={volume}
      onChange={(e) => {
        const v = Number.parseFloat(e.target.value)
        engine.setVolume(v) // instant local feedback
        usePlayer.setState({ volume: v })
        if (timer.current !== undefined) window.clearTimeout(timer.current)
        timer.current = window.setTimeout(() => {
          void api
            .volume(v)
            .then((s) => usePlayer.setState({ volume: s.volume }))
            .catch(() => {})
        }, 300)
      }}
      style={{ '--range-percent': `${Math.round(volume * 100)}%` } as CSSProperties}
      className={className}
    />
  )
}

// Queued metadata edit for the playing file (saved while it played, applies
// on track change so playback stays gapless): tiny line under the artist;
// click discards it. Null when nothing is stashed for this file.
export function PendingEditChip({ file }: { file: string }) {
  const queryClient = useQueryClient()
  const { data } = useQuery({
    queryKey: ['pending-edits', file],
    queryFn: api.pendingEdits,
    staleTime: 1000 * 30,
  })
  if (!(data ?? []).some((p) => p.file === file)) return null
  const discard = (e: MouseEvent) => {
    e.stopPropagation()
    void api
      .discardPendingEdit(file)
      .then(() => queryClient.invalidateQueries({ queryKey: ['pending-edits'] }))
      .catch(() => {})
  }
  return (
    <button
      onClick={discard}
      title="Edit queued — applies when this track changes. Click to discard."
      className="text-[10px] font-semibold uppercase tracking-[0.15em] text-orange-300/90 hover:text-orange-200 transition-colors cursor-pointer"
    >
      Edit queued
    </button>
  )
}

// Bottom player bar cloned from Music Player/ui/index.html #controls
// (h-28, orange top border, glass). Audio renders server state; crossfade
// and gapless arrive in a later phase — manual next/prev stay instant.
// Mobile stacks a compact transport row over a full-width progress slider
// (repeat/shuffle/volume stay on sm+ where they fit).
export default function PlayerBar({
  onToggleQueue,
  onToggleLyrics,
  onOpenNowPlaying,
  queueActive = false,
  lyricsActive = false,
}: {
  onToggleQueue: () => void
  onToggleLyrics: () => void
  onOpenNowPlaying: () => void
  queueActive?: boolean
  lyricsActive?: boolean
}) {
  // Granular subscriptions: the 500ms progress ticker writes store state
  // elsewhere, so subscribing field-by-field keeps the bar from re-rendering
  // on every unrelated change (queue edits, position polls).
  const currentSong = usePlayer((s) => s.currentSong)
  const isPlaying = usePlayer((s) => s.isPlaying)
  const volume = usePlayer((s) => s.volume)
  const shuffle = usePlayer((s) => s.shuffle)
  const repeat = usePlayer((s) => s.repeat)
  const source = usePlayer((s) => s.source)
  const toggle = usePlayer((s) => s.toggle)
  const boot = usePlayer((s) => s.boot)
  const navigate = useNavigate()
  // Podcast chapters: auto-skip pump + chapter-aware transport arrows.
  useChapterAutoSkip()
  const chapterJump = useChapterJump()
  const songMenuRef = useRef<SongMenuHandle | null>(null)
  const openSongMenu = (x: number, y: number) => songMenuRef.current?.openAt(x, y)
  const goArtist = (e: React.MouseEvent, artist: string) => {
    e.stopPropagation()
    navigate(`/artists/${encodeURIComponent(artist)}`)
  }
  useEffect(() => {
    void boot().catch(() => {})
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // OS media integration (desktop SMTC overlay): lock-screen / headset
  // metadata + play/pause/next/prev handlers. Cover falls back to the
  // embedded art the server already attached to the current song.
  useEffect(() => {
    if (!('mediaSession' in navigator)) return
    const song = usePlayer.getState().currentSong
    try {
      navigator.mediaSession.metadata = song
        ? new MediaMetadata({
            title: song.title,
            artist: song.artist,
            album: song.album !== 'Unknown' ? song.album : undefined,
            artwork: song.coverArt ? [{ src: song.coverArt }] : undefined,
          })
        : null
    } catch {
      /* artwork decoding must never break playback */
    }
  }, [currentSong])

  useEffect(() => {
    if (!('mediaSession' in navigator)) return
    const toggle = () => void usePlayer.getState().toggle()
    const next = () =>
      void api.next().then((s) => {
        usePlayer.setState(s)
        usePlayer.getState().syncAudio()
      })
    const prev = () =>
      void api.prev().then((s) => {
        usePlayer.setState(s)
        usePlayer.getState().syncAudio()
      })
    try {
      navigator.mediaSession.setActionHandler('play', toggle)
      navigator.mediaSession.setActionHandler('pause', toggle)
      navigator.mediaSession.setActionHandler('previoustrack', prev)
      navigator.mediaSession.setActionHandler('nexttrack', next)
    } catch {
      /* unsupported actions are fine */
    }
  }, [])

  useEffect(() => {
    if (!('mediaSession' in navigator)) return
    try {
      navigator.mediaSession.playbackState = isPlaying ? 'playing' : 'paused'
    } catch {
      /* ignore */
    }
  }, [isPlaying])

  const song = currentSong

  const doNext = () => {
    // Podcasts with chapters: the arrow jumps to the next chapter start
    // (chapter seek, not a track change) — even with auto-skip off. Past
    // the last chapter it falls through to the next track.
    if (chapterJump.next()) return
    void api.next().then((s) => {
      usePlayer.setState(s)
      usePlayer.getState().syncAudio() // hard switch; cancels any fade
    })
  }
  const doPrev = () => {
    // Same deal backwards (restarts the chapter when well inside it).
    if (chapterJump.prev()) return
    void api.prev().then((s) => {
      usePlayer.setState(s)
      usePlayer.getState().syncAudio() // hard switch; cancels any fade
    })
  }
  const doRepeat = () => void api.repeat().then((s) => usePlayer.setState(s))
  const doShuffle = () => void api.shuffle().then((s) => usePlayer.setState(s))
  const doMute = () => {
    if (volume > 0) {
      engine.setVolume(0)
      void api.volume(0).then((s) => usePlayer.setState({ volume: s.volume }))
    } else {
      engine.setVolume(1.0)
      void api.volume(1.0).then((s) => usePlayer.setState({ volume: s.volume }))
    }
  }

  return (
    <div
      id="controls"
      className="fixed bottom-0 left-0 right-0 flex flex-col min-[1100px]:flex-row min-[1100px]:items-center min-[1100px]:justify-between gap-1 min-[1100px]:gap-0 border-t border-orange-500/10 bg-black/40 backdrop-blur-2xl px-3 min-[1100px]:px-6 py-2 min-[1100px]:py-0 min-[1100px]:h-28 z-20 shadow-[0_-10px_40px_rgba(0,0,0,0.5)]"
    >
      {/* The AudioEngine owns its elements (double-buffered for crossfade);
          UI polls engine time — no DOM <audio> needed here. */}
      {/* Compact stacked layout (below 1100px): song + centered transport, progress below */}
      <div className="grid min-[1100px]:hidden grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] items-center gap-2 min-w-0">
        <div
          className="flex items-center min-w-0 gap-2 cursor-pointer rounded-xl px-1 py-0.5 active:bg-white/5 transition-colors"
          onClick={onOpenNowPlaying}
          onContextMenu={(e) => {
            e.preventDefault()
            openSongMenu(e.clientX, e.clientY)
          }}
          title="Open Now Playing"
        >
          <CoverArt
            src={song?.coverArt}
            file={song?.file}
            isPodcast={song?.isPodcast}
            alt={song?.title ?? ''}
            className="w-10 h-10 rounded-lg flex-shrink-0"
          />
          <div className="min-w-0">
            <div className="text-sm font-medium text-zinc-100 truncate">{song?.title ?? 'Nothing playing'}</div>
            {song && song.artist !== 'Unknown' ? (
              <button
                onClick={(e) => goArtist(e, song.artist)}
                className="text-xs text-zinc-500 truncate hover:text-orange-400 transition-colors cursor-pointer"
              >
                {song.artist}
              </button>
            ) : (
              <div className="text-xs text-zinc-500 truncate">{song?.artist ?? 'Pick a song to start'}</div>
            )}
            {song && <PendingEditChip file={song.file} />}
          </div>
        </div>
        <div className="flex items-center justify-center gap-1">
          <ControlButton icon="prev" title="Previous Song" iconClass="w-5 h-5" onClick={doPrev} />
          <PlayPauseButton playing={isPlaying} id="playPauseBtn-mobile" small onClick={() => void toggle()} />
          <ControlButton icon="next" title="Next Song" iconClass="w-5 h-5" onClick={doNext} />
        </div>
        <div className="flex items-center justify-end">
          <button
            onClick={onToggleLyrics}
            title="Toggle Lyrics"
            className={`cursor-pointer transition-colors p-2 rounded-full active:bg-white/5 focus:outline-none flex-shrink-0 ${
              lyricsActive ? 'text-orange-400' : 'text-zinc-400 active:text-white'
            }`}
          >
            <Icon name="musicNote" className="w-5 h-5" />
          </button>
          <button
            onClick={onToggleQueue}
            title="Toggle Queue"
            className={`cursor-pointer transition-colors p-2 rounded-full active:bg-white/5 focus:outline-none flex-shrink-0 ${
              queueActive ? 'text-orange-400' : 'text-zinc-400 active:text-white'
            }`}
          >
            <Icon name="queue" className="w-5 h-5" />
          </button>
        </div>
      </div>
      {/* Compact stacked layout (below 1100px): full-width progress + volume */}
      <div className="flex min-[1100px]:hidden w-full items-center gap-2">
        <ProgressSlider idPrefix="m" />
      </div>
      <div className="flex min-[1100px]:hidden w-full items-center gap-2">
        <button
          onClick={doMute}
          title="Mute / unmute"
          className="cursor-pointer text-zinc-400 active:text-white transition-colors p-1 rounded-full active:bg-white/5 focus:outline-none flex-shrink-0"
        >
          <Icon
            name={volume === 0 ? 'volumeMuted' : volume < 0.5 ? 'volumeLow' : 'volume'}
            className="w-4 h-4"
          />
        </button>
        <VolumeSlider
          volume={volume}
          className="flex-1 cursor-pointer outline-none border-none shadow-none focus:outline-none focus:ring-0"
        />
      </div>

      {/* Desktop: song card */}
      <div
        className="hidden min-[1100px]:flex flex-1 items-center min-w-0 gap-3 cursor-pointer rounded-xl px-2 py-1 -ml-2 hover:bg-white/5 transition-colors"
        onClick={onOpenNowPlaying}
        onContextMenu={(e) => {
          e.preventDefault()
          openSongMenu(e.clientX, e.clientY)
        }}
        title="Open Now Playing"
      >
        <CoverArt
          src={song?.coverArt}
          file={song?.file}
          isPodcast={song?.isPodcast}
          alt={song?.title ?? ''}
          className="w-14 h-14 rounded-lg flex-shrink-0"
        />
        <div className="min-w-0">
          <div className="text-sm font-medium text-zinc-100 truncate">{song?.title ?? 'Nothing playing'}</div>
          {song && song.artist !== 'Unknown' ? (
            <button
              onClick={(e) => goArtist(e, song.artist)}
              className="text-xs text-zinc-500 truncate hover:text-orange-400 transition-colors cursor-pointer"
            >
              {song.artist}
            </button>
          ) : (
            <div className="text-xs text-zinc-500 truncate">{song?.artist ?? 'Pick a song to start'}</div>
          )}
          {song && <PendingEditChip file={song.file} />}
        </div>
      </div>
      {song && (
        <SongMenu
          ref={(h) => {
            songMenuRef.current = h
          }}
          song={song}
          sourceType={source?.type ?? 'all_songs'}
          hideButton
        />
      )}

      {/* Desktop: transport + progress */}
      <div className="hidden min-[1100px]:flex flex-col items-center justify-center flex-[2] max-w-3xl gap-2">
        <div className="flex flex-row items-center justify-center gap-6 sm:gap-8 w-full">
          <ControlButton
            icon="repeat"
            title="Toggle Repeat"
            active={repeat}
            iconClass="w-5 h-5"
            onClick={doRepeat}
          />
          <ControlButton icon="prev" title="Previous Song" onClick={doPrev} />
          <PlayPauseButton playing={isPlaying} onClick={() => void toggle()} />
          <ControlButton icon="next" title="Next Song" onClick={doNext} />
          <ControlButton
            icon="shuffle"
            title="Toggle Shuffle"
            active={shuffle}
            onClick={doShuffle}
          />
        </div>
        <div className="w-full flex items-center gap-3">
          <ProgressSlider idPrefix="d" />
        </div>
      </div>

      {/* Desktop: lyrics / queue / volume */}
      <div className="hidden min-[1100px]:flex flex-1 items-center justify-end gap-2 min-w-0">
        <button
          onClick={onToggleLyrics}
          title="Toggle Lyrics"
          className={`cursor-pointer transition-all duration-300 hover:scale-110 active:scale-95 p-2 rounded-full hover:bg-white/5 focus:outline-none ${
            lyricsActive ? 'text-orange-400 hover:text-orange-300' : 'text-zinc-400 hover:text-white'
          }`}
        >
          <Icon name="musicNote" className="w-5 h-5" />
        </button>
        <button
          onClick={onToggleQueue}
          title="Toggle Queue"
          className={`cursor-pointer transition-all duration-300 hover:scale-110 active:scale-95 p-2 rounded-full hover:bg-white/5 focus:outline-none ${
            queueActive ? 'text-orange-400 hover:text-orange-300' : 'text-zinc-400 hover:text-white'
          }`}
        >
          <Icon name="queue" className="w-5 h-5" />
        </button>
        <button
          onClick={doMute}
          title="Mute / unmute"
          className="cursor-pointer text-zinc-400 hover:text-white transition-all duration-300 hover:scale-110 active:scale-95 p-2 rounded-full hover:bg-white/5 focus:outline-none"
        >
          <Icon
            name={volume === 0 ? 'volumeMuted' : volume < 0.5 ? 'volumeLow' : 'volume'}
            className="w-5 h-5"
          />
        </button>
        <VolumeSlider
          volume={volume}
          id="volumeSlider"
          className="w-20 sm:w-28 cursor-pointer outline-none border-none shadow-none focus:outline-none focus:ring-0"
        />
      </div>
    </div>
  )
}
