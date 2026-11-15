import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import { useAiDj } from '../store/aiDj'
import { useStartDjSet } from '../hooks/useStartDjSet'
import { usePlayer } from '../store/player'
import Icon from '../components/ui/icons'
import { EmptyState, LoadingState } from '../components/ui/states'
import StripCard from '../components/ui/StripCard'
import ViewHeader from '../components/ui/ViewHeader'

const cardClass =
  'group relative rounded-3xl overflow-hidden p-6 sm:p-7 flex flex-col gap-6 bg-zinc-900/40 border border-white/5 hover:border-orange-500/40 backdrop-blur-2xl shadow-[0_8px_32px_rgba(0,0,0,0.5)] transition-all duration-300 min-h-[260px]'

const playButtonClass =
  'pointer-events-auto flex h-11 w-11 cursor-pointer items-center justify-center rounded-full bg-orange-500 text-white shadow-lg shadow-orange-500/40 transition-all duration-300 hover:scale-105 hover:bg-orange-400 active:scale-95 disabled:cursor-wait disabled:opacity-40'

// Home dashboard cloned from Music Player/ui/views/home.html:
// All Songs + Daily Mix + AI DJ + Podcasts cards, then Recently Played/Downloaded strips.
export default function Home() {
  const playSong = usePlayer((s) => s.playSong)
  const playSongByFiles = usePlayer((s) => s.playSongByFiles)
  const dj = useAiDj((s) => s.state)
  const djLoading = useAiDj((s) => s.loading)
  const { start: startDj, error: djError } = useStartDjSet()
  const navigate = useNavigate()

  const { data: count } = useQuery({
    queryKey: ['song-count'],
    queryFn: () => api.songCount(),
  })
  const { data: recent, isLoading: recentLoading } = useQuery({
    queryKey: ['songs-recent'],
    queryFn: () => api.recentlyDownloaded(15),
  })
  const { data: played, isLoading: playedLoading } = useQuery({
    queryKey: ['songs-recently-played'],
    queryFn: () => api.recentlyPlayed(15),
  })
  const { data: mix } = useQuery({ queryKey: ['daily-mix'], queryFn: api.dailyMix })
  const { data: podcastCount } = useQuery({
    queryKey: ['home-podcast-count'],
    queryFn: () => api.podcastCount(),
  })

  const djActive = dj?.status === 'active'
  const mixSongs = mix?.songs ?? []

  const playMix = () => {
    if (mixSongs.length === 0) return
    void playSong(mixSongs[0], mixSongs, { type: 'daily_mix', id: mix?.date ?? null })
  }

  const playDj = async () => {
    if (await startDj('')) navigate('/dj')
  }

  const playPodcasts = async () => {
    try {
      const files = await api.podcastFiles()
      const first = files[0]
      if (!first) return
      const episode = await api.podcastDetails(first)
      await playSongByFiles(
        { ...episode, isPodcast: true },
        files,
        { type: 'podcast', id: first },
      )
    } catch {
      // Playback reconverges through the store.
    }
  }

  return (
    <div className="max-w-6xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24 flex flex-col gap-8">
      <ViewHeader
        icon="home"
        title="Home"
        subtitle={new Date().toLocaleDateString(undefined, {
          weekday: 'long',
          month: 'long',
          day: 'numeric',
        })}
      />

      <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
        <Link to="/songs" className={cardClass}>
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div className="absolute -right-16 -top-16 w-64 h-64 bg-orange-500/10 rounded-full blur-[80px] pointer-events-none" />

          <div className="pointer-events-none relative z-10 flex items-start justify-between">
            <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-orange-400 to-orange-600 flex items-center justify-center shadow-lg shadow-orange-500/30 group-hover:scale-110 transition-transform duration-300">
              <Icon name="musicNote" className="w-7 h-7 text-white" />
            </div>
            <div className="w-12 h-12 rounded-full bg-orange-500 text-white flex items-center justify-center shadow-lg shadow-orange-500/40 opacity-0 group-hover:opacity-100 translate-y-2 group-hover:translate-y-0 transition-all duration-300">
              <Icon name="play" className="w-6 h-6 ml-0.5" />
            </div>
          </div>

          <div className="relative z-10 mt-auto">
            <h2 className="text-2xl font-bold text-zinc-100 group-hover:text-orange-400 transition-colors">All Songs</h2>
            <p className="text-sm text-zinc-500 mt-1">
              {count === undefined ? 'Loading library...' : `${count} song${count === 1 ? '' : 's'} in your library`}
            </p>
            <p className="text-xs text-zinc-600 mt-3 leading-relaxed">Browse, search and sort your full library.</p>
          </div>
        </Link>

        <div className={cardClass}>
          <Link to="/mix" aria-label="Open Daily Mix" className="absolute inset-0 z-0" />
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div className="absolute -right-16 -top-16 w-64 h-64 bg-orange-600/15 rounded-full blur-[80px] pointer-events-none" />

          <div className="pointer-events-none relative z-10 flex items-start justify-between">
            <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-amber-400 to-orange-600 flex items-center justify-center shadow-lg shadow-orange-500/30 group-hover:scale-110 transition-transform duration-300">
              <Icon name="calendar" className="w-7 h-7 text-white" />
            </div>
            <div className="pointer-events-auto relative z-20 flex items-center gap-2">
              <span className="text-[11px] font-semibold uppercase tracking-widest px-2.5 py-1 rounded-full bg-orange-500/15 text-orange-300 border border-orange-500/30">
                Today
              </span>
              <button
                type="button"
                onClick={playMix}
                disabled={mixSongs.length === 0}
                title="Play Daily Mix"
                aria-label="Play Daily Mix"
                className={playButtonClass}
              >
                <Icon name="play" className="w-5 h-5 ml-0.5" />
              </button>
            </div>
          </div>

          <div className="pointer-events-none relative z-10 mt-auto">
            <h2 className="text-2xl font-bold text-zinc-100 group-hover:text-orange-400 transition-colors">Daily Mix</h2>
            <p className="text-sm text-zinc-500 mt-1">
              {mix ? `${mix.songs.length} songs - ${mix.date}` : 'Generating your mix...'}
            </p>
            <p className="text-xs text-zinc-600 mt-3 leading-relaxed">Fresh picks for today.</p>
          </div>
        </div>

        <div className={cardClass}>
          <Link to="/dj" aria-label="Open Vohithe AI DJ" className="absolute inset-0 z-0" />
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div className="absolute -right-16 -top-16 w-64 h-64 bg-orange-500/15 rounded-full blur-[80px] pointer-events-none" />

          <div className="pointer-events-none relative z-10 flex items-start justify-between">
            <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-orange-400 to-rose-500 flex items-center justify-center shadow-lg shadow-orange-500/30 group-hover:scale-110 transition-transform duration-300">
              <Icon name="sparkles" className="w-7 h-7 text-white" />
            </div>
            <div className="pointer-events-auto relative z-20 flex items-center gap-2">
              <span className={`text-[11px] font-semibold uppercase tracking-widest px-2.5 py-1 rounded-full border ${
                djActive
                  ? 'bg-orange-500/15 text-orange-300 border-orange-500/30'
                  : 'bg-white/[0.04] text-zinc-500 border-white/10'
              }`}>
                {djActive ? 'On air' : 'AI'}
              </span>
              <button
                type="button"
                onClick={() => void playDj()}
                disabled={djLoading || !dj?.enabled}
                title={djActive ? 'Start a new AI DJ set' : 'Start AI DJ'}
                aria-label={djActive ? 'Start a new AI DJ set' : 'Start AI DJ'}
                className={playButtonClass}
              >
                <Icon name="play" className="w-5 h-5 ml-0.5" />
              </button>
            </div>
          </div>

          <div className="pointer-events-none relative z-10 mt-auto">
            <h2 className="text-2xl font-bold text-zinc-100 group-hover:text-orange-400 transition-colors">AI DJ</h2>
            <p className="text-sm text-zinc-500 mt-1">
              Vohithe picks the set and speaks the transitions.
            </p>
            <p className="text-xs text-zinc-600 mt-3 leading-relaxed">Always live, never on shuffle.</p>
          </div>
        </div>

        <div className={cardClass}>
          <Link to="/podcasts" aria-label="Open Podcasts" className="absolute inset-0 z-0" />
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div className="absolute -right-16 -top-16 w-64 h-64 bg-amber-500/10 rounded-full blur-[80px] pointer-events-none" />

          <div className="pointer-events-none relative z-10 flex items-start justify-between">
            <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-amber-400 to-orange-600 flex items-center justify-center shadow-lg shadow-orange-500/30 group-hover:scale-110 transition-transform duration-300">
              <Icon name="mic" className="w-7 h-7 text-white" />
            </div>
            <div className="pointer-events-auto relative z-20 flex items-center gap-2">
              <span className="text-[11px] font-semibold uppercase tracking-widest px-2.5 py-1 rounded-full bg-orange-500/15 text-orange-300 border border-orange-500/30">
                {podcastCount ?? 0} episodes
              </span>
              <button
                type="button"
                onClick={() => void playPodcasts()}
                disabled={!podcastCount}
                title="Play podcasts"
                aria-label="Play podcasts"
                className={playButtonClass}
              >
                <Icon name="play" className="w-5 h-5 ml-0.5" />
              </button>
            </div>
          </div>

          <div className="pointer-events-none relative z-10 mt-auto">
            <h2 className="text-2xl font-bold text-zinc-100 group-hover:text-orange-400 transition-colors">Podcasts</h2>
            <p className="text-sm text-zinc-500 mt-1">
              Catch up on your saved shows.
            </p>
            <p className="text-xs text-zinc-600 mt-3 leading-relaxed">
              {podcastCount === undefined
                ? 'Loading episodes...'
                : `${podcastCount} episode${podcastCount === 1 ? '' : 's'} ready to play.`}
            </p>
          </div>
        </div>
      </div>

      {djError && (
        <p className="rounded-xl border border-red-400/20 bg-red-500/10 p-3 text-sm text-red-300">
          {djError}
        </p>
      )}

      <div className="flex flex-col gap-4">
        <div className="flex items-center justify-between px-1">
          <h2 className="text-xl font-bold text-zinc-100">Recently Played</h2>
          <Link
            to="/history"
            className="text-xs font-semibold uppercase tracking-widest text-zinc-500 hover:text-orange-400 transition-colors"
          >
            See all
          </Link>
        </div>
        {playedLoading ? (
          <LoadingState label="Loading..." />
        ) : (played ?? []).length === 0 ? (
          <div className="py-8 text-sm text-zinc-500">Nothing played yet - play something and it will show up here.</div>
        ) : (
          <div className="flex gap-4 overflow-x-auto pb-3">
            {(played ?? []).map((song) => (
              <StripCard
                key={song.file}
                song={song}
                menuSourceType="recently_played"
                onPlay={() => void playSong(song, played ?? [], { type: 'recently_played', id: null })}
              />
            ))}
          </div>
        )}
      </div>

      <div className="flex flex-col gap-4">
        <div className="flex items-center justify-between px-1">
          <h2 className="text-xl font-bold text-zinc-100">Recently Downloaded</h2>
          <Link
            to="/history"
            className="text-xs font-semibold uppercase tracking-widest text-zinc-500 hover:text-orange-400 transition-colors"
          >
            See all
          </Link>
        </div>
        {recentLoading ? (
          <LoadingState label="Loading..." />
        ) : (recent ?? []).length === 0 ? (
          <EmptyState
            title="Nothing downloaded yet"
            hint="Grab some music and it will show up here."
          />
        ) : (
          <div className="flex gap-4 overflow-x-auto pb-3">
            {(recent ?? []).map((song) => (
              <StripCard
                key={song.file}
                song={song}
                menuSourceType="recently_downloaded"
                onPlay={() => void playSong(song, recent ?? [], { type: 'recently_downloaded', id: null })}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

