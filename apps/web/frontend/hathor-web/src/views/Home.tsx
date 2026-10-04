import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import { usePlayer } from '../store/player'
import Icon from '../components/ui/icons'
import { EmptyState, LoadingState } from '../components/ui/states'
import StripCard from '../components/ui/StripCard'
import ViewHeader from '../components/ui/ViewHeader'

// Home dashboard cloned from Music Player/ui/views/home.html:
// All Songs card + Daily Mix card + Recently Played/Downloaded strips.
export default function Home() {
  const playSong = usePlayer((s) => s.playSong)
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
        <Link
          to="/songs"
          className="group relative rounded-3xl overflow-hidden p-6 sm:p-8 flex flex-col gap-6 bg-zinc-900/40 border border-white/5 hover:border-orange-500/40 backdrop-blur-2xl shadow-[0_8px_32px_rgba(0,0,0,0.5)] transition-all duration-300 min-h-[260px]"
        >
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div className="absolute -right-16 -top-16 w-64 h-64 bg-orange-500/10 rounded-full blur-[80px] pointer-events-none" />

          <div className="relative z-10 flex items-start justify-between">
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
              {count === undefined ? 'Loading library…' : `${count} song${count === 1 ? '' : 's'} in your library`}
            </p>
            <p className="text-xs text-zinc-600 mt-3 leading-relaxed">Browse, search and sort your full library.</p>
          </div>
        </Link>
        <Link
          to="/mix"
          className="group relative rounded-3xl overflow-hidden p-6 sm:p-8 flex flex-col gap-6 bg-zinc-900/40 border border-white/5 hover:border-orange-500/40 backdrop-blur-2xl shadow-[0_8px_32px_rgba(0,0,0,0.5)] transition-all duration-300 hover:shadow-orange-500/10 min-h-[260px]"
        >
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div className="absolute -right-16 -top-16 w-64 h-64 bg-orange-600/15 rounded-full blur-[80px] pointer-events-none" />

          <div className="relative z-10 flex items-start justify-between">
            <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-amber-400 to-orange-600 flex items-center justify-center shadow-lg shadow-orange-500/30 group-hover:scale-110 transition-transform duration-300">
              <Icon name="sparkles" className="w-7 h-7 text-white" />
            </div>
            <span className="text-[11px] font-semibold uppercase tracking-widest px-2.5 py-1 rounded-full bg-orange-500/15 text-orange-300 border border-orange-500/30">
              Today
            </span>
          </div>

          <div className="relative z-10 mt-auto">
            <h2 className="text-2xl font-bold text-zinc-100 group-hover:text-orange-400 transition-colors">Daily Mix</h2>
            <p className="text-sm text-zinc-500 mt-1">
              {mix ? `${mix.songs.length} songs · ${mix.date}` : 'Generating your mix…'}
            </p>
            <p className="text-xs text-zinc-600 mt-3 leading-relaxed">Fresh every day.</p>
          </div>
        </Link>
      </div>

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
          <LoadingState label="Loading…" />
        ) : (played ?? []).length === 0 ? (
          <div className="py-8 text-sm text-zinc-500">Nothing played yet — play something and it will show up here.</div>
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
          <LoadingState label="Loading…" />
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
