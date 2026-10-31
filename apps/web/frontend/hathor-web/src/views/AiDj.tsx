import { useCallback, useState } from 'react'
import { usePlayer, releaseDjHold } from '../store/player'
import { useAiDj } from '../store/aiDj'
import { engine } from '../audio/engine'
import { useDjVibeRecorder } from '../hooks/useDjVibeRecorder'
import { useStartDjSet } from '../hooks/useStartDjSet'
import CoverArt from '../components/ui/CoverArt'
import Icon from '../components/ui/icons'
import { GhostButton, PrimaryButton } from '../components/ui/fields'
import type { Song } from '../api/client'
import vohitheIcon from '../assets/vohithe-icon.jpg'

function NowPlayingCard({ song }: { song: Song | null }) {
  if (!song) return null
  return (
    <div className="mt-4 flex items-center gap-3 rounded-2xl border border-orange-500/20 bg-orange-500/[0.06] p-3">
      <CoverArt
        src={song.coverArt}
        file={song.file}
        isPodcast={song.isPodcast}
        alt={song.title}
        className="h-14 w-14 flex-shrink-0 rounded-xl"
      />
      <div className="min-w-0 flex-1">
        <div className="text-[10px] font-semibold uppercase tracking-[0.2em] text-orange-300">
          Now playing
        </div>
        <div className="truncate text-sm font-medium text-zinc-100">{song.title}</div>
        <div className="truncate text-xs text-zinc-400">{song.artist}</div>
      </div>
    </div>
  )
}

export default function AiDj() {
  const dj = useAiDj((s) => s.state)
  const loading = useAiDj((s) => s.loading)
  const voicePlaying = useAiDj((s) => s.voicePlaying)
  const waiting = useAiDj((s) => s.waiting)
  const currentSong = usePlayer((s) => s.currentSong)
  const [brief, setBrief] = useState('')
  const { start: startDj, error } = useStartDjSet()
  const appendVibe = useCallback((text: string) => {
    setBrief((current) => (current ? `${current} ${text}` : text))
  }, [])
  const recorder = useDjVibeRecorder(appendVibe)

  const active = dj?.status === 'active'
  const current = dj?.currentBatch
  const next = dj?.nextBatch

  const stopDj = async () => {
    releaseDjHold()
    engine.stopVoice()
    await useAiDj.getState().stop()
  }

  return (
    <div className="max-w-6xl mx-auto pb-8">
      <div className="relative overflow-hidden rounded-[2rem] border border-white/10 bg-gradient-to-br from-orange-500/[0.14] via-zinc-900/60 to-zinc-950 p-6 sm:p-9 mb-6">
        <div className="absolute -right-24 -top-24 h-72 w-72 rounded-full bg-orange-500/20 blur-[90px]" />
        <div className="absolute -left-20 bottom-0 h-56 w-56 rounded-full bg-fuchsia-500/10 blur-[80px]" />
        <div className="relative flex flex-col sm:flex-row sm:items-center gap-5">
          <div className="relative h-24 w-24 flex-shrink-0">
            <div className="absolute inset-0 rounded-full bg-gradient-to-br from-orange-300 via-orange-500 to-rose-500 blur-md opacity-70" />
            <div className="relative flex h-24 w-24 items-center justify-center overflow-hidden rounded-full border border-white/20 bg-zinc-950/80 shadow-2xl">
              <img
                src={vohitheIcon}
                alt="Vohithe"
                className="h-full w-full object-cover"
              />
            </div>
            <span className={`absolute right-0 bottom-1 h-4 w-4 rounded-full border-2 border-zinc-950 ${
              waiting ? 'bg-amber-400' : voicePlaying ? 'bg-emerald-400' : 'bg-orange-400'
            }`} />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-xs uppercase tracking-[0.35em] text-orange-300/80">Hathor private radio</div>
            <h1 className="mt-1 text-3xl sm:text-4xl font-semibold text-white">Vohithe</h1>
            <div className="mt-4 flex flex-wrap items-center gap-2 text-xs">
              <span className={`rounded-full border px-3 py-1 ${
                active ? 'border-orange-500/30 bg-orange-500/10 text-orange-300' : 'border-white/10 bg-white/[0.04] text-zinc-400'
              }`}>
                {active ? 'On air' : dj?.status === 'stopped' ? 'Off air' : 'Ready'}
              </span>
              {dj?.settings?.language && (
                <span className="rounded-full border border-white/10 bg-white/[0.04] px-3 py-1 text-zinc-400">
                  {dj.settings.language === 'pt-BR' ? 'Português' : 'English'}
                </span>
              )}
            </div>
          </div>
        </div>
      </div>

      {!dj?.enabled && (
        <p className="mb-4 rounded-xl border border-amber-500/20 bg-amber-500/10 p-3 text-sm text-amber-200">
          AI DJ is disabled in Hathor settings.
        </p>
      )}
      {dj?.enabled && !dj.configured && (
        <p className="mb-4 rounded-xl border border-amber-500/20 bg-amber-500/10 p-3 text-sm text-amber-200">
          Vohithe's planner is not configured. Set <code>AiDj:Token</code> and the assistant URL in
          Hathor secrets to let her choose and speak.
        </p>
      )}

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_360px]">
        <section className="rounded-3xl border border-white/10 bg-black/25 p-5 sm:p-6 backdrop-blur-xl">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-xs uppercase tracking-[0.25em] text-zinc-500">Set builder</div>
              <h2 className="mt-1 text-xl font-semibold text-zinc-100">
                {active ? 'Tell her what to play next' : 'Give her a vibe'}
              </h2>
            </div>
            {active && (
              <span className={`flex items-center gap-2 text-xs ${waiting ? 'text-amber-300' : voicePlaying ? 'text-orange-300' : 'text-zinc-500'}`}>
                <Icon name={waiting ? 'sparkles' : voicePlaying ? 'mic' : 'sparkles'} className="w-4 h-4" />
                {waiting ? 'Waiting for voice' : voicePlaying ? 'Speaking' : 'Listening'}
              </span>
            )}
          </div>
          <p className="mt-2 text-sm text-zinc-400">
            Type a mood or speak it. Leave it blank and Vohithe will choose a fresh direction for you.
          </p>
          <div className="mt-4 rounded-2xl border border-white/10 bg-white/[0.04] p-2 focus-within:border-orange-500/50 transition-colors">
            <textarea
              value={brief}
              onChange={(e) => setBrief(e.target.value)}
              rows={3}
              autoFocus
              placeholder="e.g. warm evening indie, no sad songs, more female vocals"
              className="w-full resize-none bg-transparent px-3 py-2 text-sm text-zinc-100 placeholder-zinc-500 outline-none"
            />
            <div className="flex items-center justify-between gap-2 px-1 pb-1">
              <button
                onClick={() => void recorder.start()}
                title={recorder.recording ? 'Stop listening' : 'Speak the vibe'}
                className={`flex items-center gap-2 rounded-full px-3 py-2 text-xs font-medium transition-colors cursor-pointer ${
                  recorder.recording
                    ? 'bg-red-500/20 text-red-200'
                    : 'bg-white/[0.06] text-zinc-300 hover:bg-white/[0.12]'
                }`}
              >
                <Icon name="mic" className="w-4 h-4" />
                {recorder.recording ? 'Listening…' : 'Speak'}
              </button>
              <span className="text-[11px] text-zinc-600">
                {recorder.recording ? 'Speak naturally; chunks are transcribed as you go.' : 'Typing and voice both work'}
              </span>
            </div>
          </div>
          {recorder.error && <p className="mt-2 text-sm text-red-300">{recorder.error}</p>}
          {error && <p className="mt-2 text-sm text-red-300">{error}</p>}
          <div className="mt-4 flex flex-wrap items-center gap-2">
            <PrimaryButton onClick={() => void startDj(brief)} loading={loading} disabled={!dj?.enabled}>
              {active ? 'Start a new set' : 'Start Vohithe'}
            </PrimaryButton>
            {active && (
              <GhostButton onClick={() => void stopDj()} className="border-red-400/20 text-red-300">
                Stop DJ
              </GhostButton>
            )}
          </div>
        </section>

        <aside className="space-y-4">
          <div className="rounded-3xl border border-white/10 bg-black/25 p-5 backdrop-blur-xl">
            <div className="text-xs uppercase tracking-[0.25em] text-zinc-500">On air</div>
            <h2 className="mt-1 text-lg font-semibold text-zinc-100">
              {current ? `Set ${current.number}` : 'No set yet'}
            </h2>
            {current ? (
              <>
                <p className="mt-1 text-xs text-zinc-500">
                  {current.tracks.length} tracks · {current.status}
                </p>
                <NowPlayingCard song={currentSong} />
                {!currentSong && (
                  <p className="mt-3 text-sm text-zinc-500">
                    Waiting for the first track to start.
                  </p>
                )}
              </>
            ) : (
              <p className="mt-3 text-sm text-zinc-500">
                Start a set and Vohithe will keep the next batch ready while these songs play.
              </p>
            )}
          </div>

          {next && (
            <div className="rounded-3xl border border-white/10 bg-black/25 p-5 backdrop-blur-xl">
              <div className="flex items-center justify-between">
                <div className="text-xs uppercase tracking-[0.25em] text-zinc-500">Next set</div>
                <span className="text-xs text-zinc-500">{next.tracks.length} ready</span>
              </div>
              <p className="mt-2 text-sm text-zinc-400">
                Vohithe has the next set ready. No spoilers here — her transition will introduce it when it plays.
              </p>
            </div>
          )}

          {active && (
            <div className="flex flex-wrap gap-2">
              <GhostButton onClick={() => void useAiDj.getState().feedback('like')}>More like this</GhostButton>
              <GhostButton onClick={() => void useAiDj.getState().feedback('dislike')}>Less like this</GhostButton>
            </div>
          )}
        </aside>
      </div>
    </div>
  )
}
