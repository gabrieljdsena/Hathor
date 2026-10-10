import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useParams } from 'react-router-dom'
import { api } from '../api/client'
import { usePlayer } from '../store/player'
import LibraryTable from '../components/LibraryTable'
import Modal from '../components/ui/Modal'
import { GhostButton, PrimaryButton } from '../components/ui/fields'
import { PlayCircleButton } from '../components/ui/buttons'
import PlaylistFormModal from '../components/ui/PlaylistFormModal'
import { EmptyState, LoadingState } from '../components/ui/states'
import { PlaylistCover } from './Playlists'

// Playlist detail: header (cover + counts + play/edit/delete) + song table.
export default function PlaylistDetail() {
  const { id } = useParams()
  const playlistId = Number(id)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const playSong = usePlayer((s) => s.playSong)
  const [editOpen, setEditOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)

  const { data: playlists } = useQuery({ queryKey: ['playlists'], queryFn: api.playlists })
  const playlist = playlists?.find((p) => p.id === playlistId)
  const { data: songs, isLoading } = useQuery({
    queryKey: ['playlist-songs', playlistId],
    queryFn: () => api.playlistSongs(playlistId),
    enabled: Number.isFinite(playlistId),
  })

  const playAll = () => {
    const first = songs?.[0]
    if (first)
      void playSong(first, songs ?? [], {
        type: 'playlist',
        id: String(playlistId),
      })
  }

  if (!Number.isFinite(playlistId)) return <EmptyState title="Playlist not found" />

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        {playlists === undefined ? (
          <LoadingState label="Loading playlist…" />
        ) : !playlist ? (
          <EmptyState title="Playlist not found" hint="It may have been deleted." />
        ) : (
          <div className="flex items-center gap-4 max-[700px]:flex-wrap max-[700px]:gap-y-3">
            <PlaylistCover src={playlist.thumbnail} title={playlist.title} />
            <div className="min-w-0 flex-1">
              <h1 className="text-2xl font-bold text-zinc-100 truncate">{playlist.title}</h1>
              {playlist.description && <p className="text-sm text-zinc-500 mt-1 truncate">{playlist.description}</p>}
              <p className="text-xs text-zinc-500 tracking-wide mt-0.5">
                {songs?.length ?? 0} song{(songs?.length ?? 0) === 1 ? '' : 's'}
              </p>
            </div>
            <div className="ml-auto flex items-center gap-2">
              <GhostButton onClick={() => setEditOpen(true)}>Edit</GhostButton>
              <GhostButton
                onClick={() => setDeleteOpen(true)}
                className="hover:bg-red-500/20 hover:text-red-400 hover:border-red-500/50"
              >
                Delete
              </GhostButton>
              <PlayCircleButton title="Play playlist" onClick={playAll} />
            </div>
          </div>
        )}
      </div>

      <LibraryTable
        songs={songs}
        isLoading={isLoading}
        loadingLabel="Loading playlist songs…"
        emptyTitle="Nothing here yet"
        emptyHint="Add songs from any song menu."
        source={{ type: 'playlist', id: String(playlistId) }}
        searchPlaceholder="Search playlist..."
        onRemoveFromPlaylist={(song) => {
          void api.removeSongFromPlaylist(playlistId, song.file).then(() => {
            void queryClient.invalidateQueries({ queryKey: ['playlist-songs', playlistId] })
            void queryClient.invalidateQueries({ queryKey: ['playlists'] })
          })
        }}
      />

      {editOpen && playlist && (
        <PlaylistFormModal
          playlist={playlist}
          onClose={() => setEditOpen(false)}
          onSaved={() => {
            setEditOpen(false)
            void queryClient.invalidateQueries({ queryKey: ['playlists'] })
          }}
        />
      )}

      <Modal open={deleteOpen} onClose={() => setDeleteOpen(false)} title="Delete playlist?">
        <p className="text-sm text-zinc-400">
          <span className="text-zinc-200 font-medium">{playlist?.title}</span> will be deleted. Songs stay in your
          library.
        </p>
        <div className="flex justify-end gap-3 pt-4 mt-4 border-t border-white/10">
          <GhostButton onClick={() => setDeleteOpen(false)}>Cancel</GhostButton>
          <PrimaryButton
            onClick={() =>
              void api
                .deletePlaylist(playlistId)
                .then(() => {
                  void queryClient.invalidateQueries({ queryKey: ['playlists'] })
                  navigate('/playlists')
                })
                .catch(() => setDeleteOpen(false))
            }
          >
            Yes, delete it
          </PrimaryButton>
        </div>
      </Modal>
    </div>
  )
}

