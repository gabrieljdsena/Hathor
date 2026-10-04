import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import CoverArt from '../components/ui/CoverArt'
import Icon from '../components/ui/icons'
import PlaylistFormModal from '../components/ui/PlaylistFormModal'
import ViewHeader from '../components/ui/ViewHeader'
import { EmptyState, LoadingState } from '../components/ui/states'
import { PrimaryButton } from '../components/ui/fields'

// Playlists grid cloned from playlist_home.html: cards + dashed create card.
export default function Playlists() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const { data: playlists, isLoading } = useQuery({
    queryKey: ['playlists', 'with-counts'],
    queryFn: api.playlistsWithCounts,
  })

  return (
    <div className="max-w-8xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24">
      <div className="flex items-center justify-between flex-wrap gap-4 mb-8">
        <ViewHeader
          icon="list"
          title="Playlists"
          subtitle="Your custom playlists"
        />
        <PrimaryButton onClick={() => setCreateOpen(true)} className="flex items-center gap-2">
          <Icon name="plus" className="w-5 h-5" />
          New Playlist
        </PrimaryButton>
      </div>

      {isLoading ? (
        <LoadingState label="Loading playlists…" />
      ) : (playlists ?? []).length === 0 ? (
        <EmptyState title="No playlists yet" hint="Create one to start collecting songs." />
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 gap-4 sm:gap-6 mt-8">
          <div
            onClick={() => setCreateOpen(true)}
            className="group relative aspect-square rounded-xl overflow-hidden border-2 border-dashed border-white/10 hover:border-orange-500/50 hover:bg-orange-500/5 transition-all duration-300 cursor-pointer flex flex-col items-center justify-center gap-3"
          >
            <div className="w-12 h-12 rounded-full bg-white/5 group-hover:bg-orange-500/20 flex items-center justify-center text-zinc-500 group-hover:text-orange-400 transition-all duration-300">
              <Icon name="plus" className="w-6 h-6" />
            </div>
            <span className="text-sm font-medium text-zinc-400 group-hover:text-orange-400 transition-colors">
              Create Playlist
            </span>
          </div>

          {(playlists ?? []).map((p) => (
            <div
              key={p.id}
              onClick={() => navigate(`/playlists/${p.id}`)}
              className="playlist-card group relative aspect-square rounded-xl overflow-hidden bg-zinc-800/40 border border-white/5 hover:border-orange-500/50 hover:bg-zinc-800/60 transition-all duration-300 cursor-pointer shadow-lg"
            >
              <div className="absolute inset-0 flex items-center justify-center text-zinc-600 group-hover:text-orange-400/80 transition-all duration-300 group-hover:scale-110 overflow-hidden">
                {p.thumbnail ? (
                  <img
                    src={p.thumbnail}
                    alt={p.title}
                    loading="lazy"
                    className="w-full h-full object-cover rounded-xl transition-all duration-300"
                  />
                ) : (
                  <Icon name="list" className="w-16 h-16" stroke />
                )}
              </div>
              <div className="absolute inset-0 bg-black/40 opacity-0 group-hover:opacity-100 transition-opacity duration-300 flex items-center justify-center">
                <div className="w-12 h-12 rounded-full bg-orange-400 hover:bg-orange-500 flex items-center justify-center text-white shadow-lg shadow-orange-500/40 transform translate-y-4 group-hover:translate-y-0 transition-transform duration-300">
                  <Icon name="play" className="w-6 h-6 ml-1" />
                </div>
              </div>
              <div className="absolute bottom-0 inset-x-0 p-4 bg-gradient-to-t from-zinc-950 via-zinc-900/80 to-transparent flex flex-col justify-end h-1/2">
                <span className="block text-zinc-200 font-medium group-hover:text-orange-400 transition-colors truncate drop-shadow-md">
                  {p.title}
                </span>
                <span className="block text-xs text-zinc-400 mt-1 font-medium">
                  {p.songCount} song{p.songCount === 1 ? '' : 's'}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}

      {createOpen && (
        <PlaylistFormModal
          onClose={() => setCreateOpen(false)}
          onSaved={(created) => {
            setCreateOpen(false)
            void queryClient.invalidateQueries({ queryKey: ['playlists'] })
            navigate(`/playlists/${created.id}`)
          }}
        />
      )}
    </div>
  )
}

// Reused cover tile for playlist detail header (kept local: detail header
// differs from grid cards by size + edit affordance).
export function PlaylistCover({ src, title }: { src: string | null; title: string }) {
  return <CoverArt src={src} alt={title} className="w-24 h-24 rounded-2xl flex-shrink-0" iconClassName="w-10 h-10 text-zinc-600" />
}
