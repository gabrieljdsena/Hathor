import { useState } from 'react'
import { api } from '../api/client'
import { usePlayer } from '../store/player'
import CoverArt from './ui/CoverArt'
import Icon from './ui/icons'
import { ControlButton } from './ui/buttons'
import { VolumeSlider } from './PlayerBar'
import { ChapterSkip, useActiveChapter, useChapterJump } from './ui/PodcastTimestamps'
import ResumeSpot, { useResumeSpot } from './ui/ResumeSpot'
import SyncedLyrics, { useLyricsOffset, useSongLyrics } from './ui/SyncedLyrics'

// Fullscreen Now Playing overlay (desktop Now Playing view): large art,
// title/artist, transport + volume. Closes to the library; audio continues.
export default function NowPlaying({ onClose }: { onClose: () => void }) {
  const song = usePlayer((s) => s.currentSong)
  const isPlaying = usePlayer((s) => s.isPlaying)
  const volume = usePlayer((s) => s.volume)
  const toggle = usePlayer((s) => s.toggle)
  const syncAudio = usePlayer((s) => s.syncAudio)
  const { offsetMs } = useLyricsOffset(song?.file)
  const chapter = useActiveChapter(song?.file, song?.isPodcast)
  const { data: lyrics, isLoading: lyricsLoading } = useSongLyrics(
    song?.file,
    chapter?.name ?? song?.title ?? '',
    chapter ? '' : (song?.artist ?? ''),
    song?.duration,
    true,
    chapter?.id ?? null,
  )
  // Lyrics-first like the original: fullscreen opens on lyrics only (no
  // sidebar, no bottom bar — the overlay covers both); the toggle reveals
  // cover + transport instead.
  const [showLyrics, setShowLyrics] = useState(true)
  // Chapter-aware arrows (same rule as the player bar): podcasts jump
  // chapter boundaries, everything else changes track.
  const chapterJump = useChapterJump()
  // Another device's paused spot (resume affordance, never auto-plays).
  const resumeSpot = useResumeSpot()

  const step = (which: 'prev' | 'next') => () => {
    if (chapterJump[which]()) return
    const call = which === 'prev' ? api.prev() : api.next()
    void call
      .then((s) => {
        usePlayer.setState(s)
        syncAudio()
      })
      .catch(() => {})
  }

  if (!song) return null
  return (
    <div className="fixed inset-0 z-40 bg-zinc-950/95 backdrop-blur-3xl flex flex-col items-center overflow-hidden">
      <div className="w-full flex items-center justify-between px-5 sm:px-8 h-16 flex-shrink-0">
        <button
          onClick={onClose}
          title="Back"
          className="flex items-center gap-2 text-zinc-400 hover:text-white transition-all duration-200 px-3 py-2 rounded-xl hover:bg-white/10 cursor-pointer"
        >
          <Icon name="x" className="w-5 h-5" />
          <span className="text-xs font-semibold uppercase tracking-[0.2em] hidden sm:inline">Now Playing</span>
        </button>
        <span className="text-xs text-zinc-500 uppercase tracking-widest truncate px-2 hidden md:inline">
          {song.title} • {song.artist}
        </span>
        <div className="flex items-center gap-1">
          <button
            onClick={() => void toggle()}
            title={isPlaying ? 'Pause' : 'Play'}
            className="text-zinc-400 hover:text-white transition-all p-2.5 rounded-xl hover:bg-white/10 cursor-pointer"
          >
            <Icon name={isPlaying ? 'pause' : 'play'} className="w-5 h-5" />
          </button>
          <button
            onClick={() => setShowLyrics((v) => !v)}
            title={showLyrics ? 'Show player' : 'Show lyrics'}
            className={`transition-all p-2.5 rounded-xl hover:bg-white/10 cursor-pointer ${
              showLyrics ? 'text-orange-400 hover:text-orange-300' : 'text-zinc-400 hover:text-white'
            }`}
          >
            <Icon name="musicNote" className="w-5 h-5" />
          </button>
        </div>
      </div>

      {resumeSpot && resumeSpot.file !== song.file && (
        <div className="w-full flex justify-center px-4 pt-1 flex-shrink-0">
          <ResumeSpot key={resumeSpot.file + resumeSpot.updatedUtc} spot={resumeSpot} />
        </div>
      )}

      {showLyrics ? (
        <div className="flex-1 min-h-0 w-full max-w-[88rem] mx-auto px-6 sm:px-10 pb-10 flex flex-col">
          <SyncedLyrics
            synced={lyrics?.synced}
            plain={lyrics?.plain}
            isLoading={lyricsLoading}
            offsetMs={offsetMs}
            chapterStartSecs={chapter?.startSecs ?? 0}
          />
        </div>
      ) : (
        <div className="flex-1 min-h-0 flex flex-col items-center justify-center gap-4 sm:gap-6 min-w-0 p-6 overflow-y-auto">
          <CoverArt
            src={song.coverArt}
            file={song.file}
            isPodcast={song.isPodcast}
            alt={song.title}
            className="w-40 h-40 sm:w-56 sm:h-56 lg:w-72 lg:h-72 rounded-2xl shadow-2xl"
            iconClassName="w-20 h-20 text-zinc-600"
          />
          <div className="text-center min-w-0 max-w-xl">
            <h1 className="text-xl sm:text-2xl lg:text-3xl font-bold text-zinc-100 truncate">{song.title}</h1>
            <p className="text-zinc-400 mt-1 truncate">
              {song.artist} {song.album !== 'Unknown' ? `• ${song.album}` : ''}
            </p>
          </div>
          <div className="flex items-center gap-6">
            <ControlButton icon="prev" title="Previous" onClick={step('prev')} />
            <button
              onClick={() => void toggle()}
              className="flex items-center justify-center w-16 h-16 bg-white text-zinc-950 hover:bg-orange-400 hover:text-white transition-all duration-300 hover:scale-105 active:scale-95 rounded-full cursor-pointer focus:outline-none"
            >
              <Icon name={isPlaying ? 'pause' : 'play'} className={`w-9 h-9${isPlaying ? '' : ' ml-1'}`} />
            </button>
            <ControlButton icon="next" title="Next" onClick={step('next')} />
          </div>
          <VolumeSlider volume={volume} className="w-56 cursor-pointer outline-none" />
          {song.isPodcast && (
            <div className="w-full max-w-xl rounded-2xl bg-black/20 backdrop-blur-2xl border border-white/5 p-3">
              <ChapterSkip file={song.file} />
            </div>
          )}
        </div>
      )}
    </div>
  )
}
