import { useCallback, useState } from 'react'
import { releaseDjHold, usePlayer } from '../store/player'
import { useAiDj } from '../store/aiDj'
import { engine } from '../audio/engine'
import { useDjVibeRecorder } from '../hooks/useDjVibeRecorder'
import { useStartDjSet } from '../hooks/useStartDjSet'
import { AudioWaveform, VohitheVisualizer } from '../components/ai-dj/VohitheVisualizer'
import CoverArt from '../components/ui/CoverArt'
import Icon from '../components/ui/icons'
import { GhostButton, PrimaryButton } from '../components/ui/fields'
import type { Song } from '../api/client'
import vohithePortrait from '../assets/vohithe-portrait.jpg'

function StatusDot({ active, waiting, voice }: { active: boolean; waiting: boolean; voice: boolean }) {
  return (
    <span
      className={`inline-block h-2 w-2 rounded-full ${
        waiting ? 'bg-amber-400' : voice ? 'bg-orange-400' : active ? 'bg-emerald-400' : 'bg-zinc-600'
      }`}
    />
  )
}

function NowPlaying({ song }: { song: Song | null }) {
  if (!song) {
    return (
      <div className="flex min-h-[132px] items-center text-sm text-zinc-500">
        Waiting for the first track.
      </div>
    )
  }
  return (
    <div className="flex min-h-[132px] items-center gap-5">
      <CoverArt
        src={song.coverArt}
        file={song.file}
        isPodcast={song.isPodcast}
        alt={song.title}
        className="h-24 w-24 flex-shrink-0 rounded-xl shadow-2xl shadow-black/40"
      />
      <div className="min-w-0">
        <div className="font-mono text-[10px] uppercase tracking-[0.24em] text-orange-300/80">
          Now playing
        </div>
        <div className="mt-2 truncate text-xl font-medium tracking-tight text-zinc-50">
          {song.title}
        </div>
        <div className="mt-1 truncate text-sm text-zinc-400">{song.artist}</div>
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
  const statusLabel = waiting ? 'Waiting for voice' : voicePlaying ? 'Speaking' : active ? 'On air' : 'Ready'

  const stopDj = async () => {
    releaseDjHold()
    engine.stopVoice()
    await useAiDj.getState().stop()
  }

  return (
    <div className="mx-auto max-w-[1400px] pb-10">
      <div className="grid items-center gap-10 lg:grid-cols-[minmax(0,1.05fr)_minmax(340px,0.95fr)] lg:gap-14">
        <section className="min-w-0">
          <div className="flex items-center gap-3 font-mono text-[10px] uppercase tracking-[0.32em] text-zinc-500">
            <span>Hathor private radio</span>
            <span className="h-px w-8 bg-orange-500/50" />
            <span className="text-orange-300/80">AI DJ</span>
          </div>

          <h1 className="mt-5 text-[clamp(4rem,10vw,8rem)] font-semibold leading-[0.82] tracking-[-0.075em] text-zinc-50">
            Vohithe
          </h1>

          <div className="mt-7 flex flex-wrap items-center gap-x-5 gap-y-3 font-mono text-[11px] uppercase tracking-[0.18em] text-zinc-400">
            <span className="inline-flex items-center gap-2">
              <StatusDot active={active} waiting={waiting} voice={voicePlaying} />
              {statusLabel}
            </span>
            <span className="text-zinc-700">/</span>
            <span>{dj?.settings?.language === 'pt-BR' ? 'Portugues' : 'English'}</span>
            {current && (
              <>
                <span className="text-zinc-700">/</span>
                <span>Set {String(current.number).padStart(2, '0')}</span>
              </>
            )}
          </div>

          <div className="mt-9 max-w-xl rounded-2xl border border-white/10 bg-zinc-900/45 p-2 shadow-2xl shadow-black/20 backdrop-blur-xl">
            <div className="px-3 pt-2 font-mono text-[9px] uppercase tracking-[0.26em] text-zinc-500">
              Set the vibe
            </div>
            <textarea
              value={brief}
              onChange={(event) => setBrief(event.target.value)}
              rows={3}
              autoFocus
              placeholder="warm evening indie, no sad songs, more female vocals"
              className="mt-1 w-full resize-none bg-transparent px-3 py-3 text-base leading-relaxed text-zinc-100 placeholder-zinc-600 outline-none"
            />
            <div className="flex flex-wrap items-center justify-between gap-2 p-1">
              <button
                onClick={() => void recorder.start()}
                title={recorder.recording ? 'Stop listening' : 'Speak the vibe'}
                className={`flex items-center gap-2 rounded-full px-3 py-2 font-mono text-[10px] uppercase tracking-[0.14em] transition-colors cursor-pointer ${
                  recorder.recording
                    ? 'bg-red-500/15 text-red-200'
                    : 'bg-white/[0.05] text-zinc-400 hover:bg-white/[0.09] hover:text-zinc-100'
                }`}
              >
                <Icon name="mic" className="h-4 w-4" />
                {recorder.recording ? 'Listening' : 'Speak'}
              </button>
              <div className="flex items-center gap-2">
                {active && (
                  <GhostButton onClick={() => void stopDj()} className="border-white/10">
                    Stop
                  </GhostButton>
                )}
                <PrimaryButton onClick={() => void startDj(brief)} loading={loading} disabled={!dj?.enabled}>
                  {active ? 'New set' : 'Start'}
                </PrimaryButton>
              </div>
            </div>
          </div>

          {recorder.error && <p className="mt-3 text-sm text-red-300">{recorder.error}</p>}
          {error && <p className="mt-3 text-sm text-red-300">{error}</p>}
          {!dj?.enabled && (
            <p className="mt-4 max-w-xl rounded-xl border border-amber-500/20 bg-amber-500/10 p-3 text-sm text-amber-200">
              AI DJ is disabled in Hathor settings.
            </p>
          )}
          {dj?.enabled && !dj.configured && (
            <p className="mt-4 max-w-xl rounded-xl border border-amber-500/20 bg-amber-500/10 p-3 text-sm text-amber-200">
              Set <code>AiDj:Token</code> and the assistant URL in Hathor secrets to let Vohithe choose and speak.
            </p>
          )}

          {active && (
            <div className="mt-5 flex flex-wrap gap-2">
              <GhostButton onClick={() => void useAiDj.getState().feedback('like')}>More like this</GhostButton>
              <GhostButton onClick={() => void useAiDj.getState().feedback('dislike')}>Less like this</GhostButton>
            </div>
          )}
        </section>

        <section className="relative mx-auto aspect-square w-full max-w-[470px]">
          <div className="absolute inset-[8%] rounded-full bg-orange-500/[0.06] blur-3xl" />
          <VohitheVisualizer
            active={active}
            voice={voicePlaying || waiting}
            className="absolute inset-0 h-full w-full"
          />
          <div className="absolute inset-[15%] overflow-hidden rounded-full border border-white/15 bg-zinc-900 shadow-[0_30px_80px_rgba(0,0,0,0.55)]">
            <img src={vohithePortrait} alt="Vohithe" className="h-full w-full object-cover" />
            <div className="absolute inset-0 rounded-full ring-1 ring-inset ring-white/10" />
          </div>
          <div className="absolute bottom-[3%] left-1/2 flex -translate-x-1/2 items-center gap-2 rounded-full border border-white/10 bg-zinc-950/85 px-4 py-2 font-mono text-[9px] uppercase tracking-[0.22em] text-zinc-300 shadow-xl backdrop-blur-xl">
            <StatusDot active={active} waiting={waiting} voice={voicePlaying} />
            {statusLabel}
          </div>
        </section>
      </div>

      <div className="mt-12 grid gap-4 md:grid-cols-2 xl:grid-cols-4">
        <section className="rounded-2xl border border-white/10 bg-zinc-900/35 p-5 md:col-span-2">
          <div className="mb-4 flex items-center justify-between">
            <div className="font-mono text-[10px] uppercase tracking-[0.24em] text-zinc-500">Now playing</div>
            <span className="font-mono text-[9px] uppercase tracking-[0.18em] text-zinc-600">
              {current ? `${current.tracks.length} track set` : 'Idle'}
            </span>
          </div>
          <NowPlaying song={currentSong} />
        </section>

        <section className="rounded-2xl border border-white/10 bg-zinc-900/35 p-5">
          <div className="font-mono text-[10px] uppercase tracking-[0.24em] text-zinc-500">Next set</div>
          <div className="mt-5 text-4xl font-semibold tracking-[-0.04em] text-zinc-100">
            {next ? String(next.tracks.length).padStart(2, '0') : '--'}
          </div>
          <div className="mt-2 text-sm text-zinc-500">
            {next ? 'tracks prepared' : 'waiting for a set'}
          </div>
          <div className="mt-5 h-px bg-white/5" />
          <div className="mt-4 flex items-center gap-2 font-mono text-[9px] uppercase tracking-[0.16em] text-zinc-600">
            <span className={`h-1.5 w-1.5 rounded-full ${next ? 'bg-orange-400' : 'bg-zinc-700'}`} />
            {next ? 'Ready for the transition' : 'Not prepared'}
          </div>
        </section>

        <section className="rounded-2xl border border-white/10 bg-zinc-900/35 p-5">
          <div className="flex items-center justify-between">
            <div className="font-mono text-[10px] uppercase tracking-[0.24em] text-zinc-500">Voice</div>
            <Icon name={voicePlaying ? 'mic' : 'sparkles'} className="h-4 w-4 text-orange-400/70" />
          </div>
          <div className="mt-6 h-14">
            <AudioWaveform active={active || voicePlaying} className="h-full w-full" />
          </div>
          <div className="mt-4 flex items-center justify-between font-mono text-[9px] uppercase tracking-[0.16em] text-zinc-600">
            <span>{statusLabel}</span>
            <span>{dj?.settings?.language === 'pt-BR' ? 'PT-BR' : 'EN-US'}</span>
          </div>
        </section>
      </div>
    </div>
  )
}
