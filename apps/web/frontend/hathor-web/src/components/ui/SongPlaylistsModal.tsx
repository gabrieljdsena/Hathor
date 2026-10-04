import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type Song } from '../../api/client'
import CoverArt from './CoverArt'
import Modal from './Modal'
import { GhostButton, PrimaryButton } from './fields'

// "Add to Playlist…" checklist cloned from ui/modals/add_playlist.html:
// all playlists with thumbnails + checked state, bulk save.
export default function SongPlaylistsModal({ song, onClose }: { song: Song; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [selected, setSelected] = useState<Set<number> | null>(null)
  const [saving, setSaving] = useState(false)

  const { data: playlists } = useQuery({ queryKey: ['playlists'], queryFn: api.playlists })
  const { data: current } = useQuery({
    queryKey: ['song-playlists', song.file],
    queryFn: () => api.songPlaylists(song.file),
  })

  const checked = selected ?? new Set(current ?? [])
  const toggle = (id: number) => {
    const next = new Set(checked)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setSelected(next)
  }

  const save = async () => {
    setSaving(true)
    try {
      await api.setSongPlaylists(song.file, song.title, [...checked])
      void queryClient.invalidateQueries({ queryKey: ['playlists'] })
      onClose()
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal open onClose={onClose} title="Add to Playlist">
      <p className="text-xs text-zinc-400 mt-1 mb-4 border-b border-white/5 pb-2 truncate font-medium max-w-[90%]">
        {song.title}
      </p>
      <div className="space-y-2.5 max-h-60 overflow-y-auto mb-6 pr-1">
        {(playlists ?? []).map((p) => (
          <label
            key={p.id}
            className="flex items-center gap-3 bg-white/[0.04] border border-white/[0.08] rounded-xl p-3 hover:bg-white/[0.08] transition-all cursor-pointer group"
          >
            {p.thumbnail ? (
              <img
                src={p.thumbnail}
                alt=""
                className="w-9 h-9 object-cover rounded-lg flex-shrink-0 border border-white/5 group-hover:scale-105 transition-transform duration-300"
              />
            ) : (
              <CoverArt src={null} className="w-9 h-9 rounded-lg flex-shrink-0" iconClassName="w-4 h-4 text-zinc-600" />
            )}
            <span className="flex-1 text-sm font-semibold text-zinc-300 group-hover:text-zinc-200 transition-colors truncate">
              {p.title}
            </span>
            <input
              type="checkbox"
              checked={checked.has(p.id)}
              onChange={() => toggle(p.id)}
              className="w-5 h-5 rounded border-zinc-700 text-orange-500 focus:ring-orange-500 bg-zinc-900 cursor-pointer accent-orange-500"
            />
          </label>
        ))}
        {(playlists ?? []).length === 0 && (
          <div className="text-center py-4">
            <p className="text-zinc-400 font-medium text-sm">No playlists found</p>
            <p className="text-xs text-zinc-500 mt-1">
              <Link to="/playlists" onClick={onClose} className="text-orange-400 hover:text-orange-300">
                Create a playlist first
              </Link>{' '}
              in the Playlists page.
            </p>
          </div>
        )}
      </div>
      <div className="flex justify-end gap-3">
        <GhostButton type="button" onClick={onClose}>
          Cancel
        </GhostButton>
        <PrimaryButton type="button" loading={saving} onClick={() => void save()}>
          {saving ? 'Saving…' : 'Save Changes'}
        </PrimaryButton>
      </div>
    </Modal>
  )
}
