import { useEffect, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, type Song } from '../../api/client'
import CoverArt, { evictCoverCache } from './CoverArt'
import Modal from './Modal'
import { GhostButton, PrimaryButton, TextField } from './fields'

// Metadata editor cloned from ui/modals/edit_song.html:
// Title/Artist/Album/Year + cover preview + file picker + Remove.
// CoverArt value sent: data: URL (new file), existing data: URL (kept),
// or the 'REMOVE' sentinel. Null fields are preserved server-side.
export default function EditSongModal({
  song,
  onClose,
  onSaved,
}: {
  song: Song
  onClose: () => void
  onSaved: (updated: Song) => void
}) {
  const queryClient = useQueryClient()
  const [title, setTitle] = useState(song.title === 'Unknown' ? '' : song.title)
  const [artist, setArtist] = useState(song.artist === 'Unknown' ? '' : song.artist)
  const [album, setAlbum] = useState(song.album === 'Unknown' ? '' : song.album)
  const [year, setYear] = useState(song.year === 'Unknown' ? '' : song.year)
  const [cover, setCover] = useState<string | null>(song.coverArt)
  const [coverRemoved, setCoverRemoved] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Lazy-load full cover when the row only carried a thumb-less entry.
  useEffect(() => {
    if (cover) return
    let cancelled = false
    const load = song.isPodcast ? api.podcastDetails(song.file) : api.song(song.file, true)
    load
      .then((full) => {
        if (!cancelled && full.coverArt) setCover(full.coverArt)
      })
      .catch(() => {})
    return () => {
      cancelled = true
    }
  }, [cover, song.file, song.isPodcast])

  const pickFile = (file: File | undefined) => {
    if (!file) return
    const reader = new FileReader()
    reader.onload = (e) => {
      setCover(typeof e.target?.result === 'string' ? e.target.result : null)
      setCoverRemoved(false)
    }
    reader.readAsDataURL(file)
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      // Episodes save through the podcasts endpoint (shared modal, own table).
      // Empty strings clear the field; untouched fields resubmit their
      // current value (server keeps null = keep for headless clients).
      const updated = song.isPodcast
        ? await api.patchPodcast(song.file, {
            title,
            artist,
            coverArt: coverRemoved ? 'REMOVE' : cover,
          })
        : (
            await api.patchSong(song.file, {
              title,
              artist,
              album,
              year,
              genre: null, // desktop modal has no genre field — preserved server-side (G1)
              coverArt: coverRemoved ? 'REMOVE' : cover,
            })
          ).song
      evictCoverCache(song.file)
      void queryClient.invalidateQueries({ queryKey: ['songs'] })
      void queryClient.invalidateQueries({ queryKey: ['podcasts'] })
      onSaved(updated)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save metadata.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal open onClose={onClose} title="Edit Song Metadata">
      <form onSubmit={submit} className="space-y-4" onClick={(e) => e.stopPropagation()}>
        <TextField label="Title" value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Song Title" />
        <TextField label="Artist" value={artist} onChange={(e) => setArtist(e.target.value)} placeholder="Artist Name" />
        <div className="grid grid-cols-2 gap-4">
          <TextField label="Album" value={album} onChange={(e) => setAlbum(e.target.value)} placeholder="Album Name" />
          <TextField label="Year" value={year} onChange={(e) => setYear(e.target.value)} placeholder="Year" />
        </div>

        <div className="flex items-center gap-4 bg-white/[0.04] p-3 rounded-xl border border-white/[0.08]">
          <CoverArt
            src={coverRemoved ? null : cover}
            alt="Cover preview"
            className="w-16 h-16 rounded-lg flex-shrink-0"
          />
          <div className="flex-grow flex items-center justify-between">
            <div className="flex-grow">
              <label className="block text-xs font-semibold text-zinc-400 uppercase tracking-wider">
                Cover Image
              </label>
              <input
                type="file"
                accept="image/*"
                onChange={(e) => pickFile(e.target.files?.[0])}
                className="mt-1 block w-full text-xs text-zinc-300 cursor-pointer bg-transparent outline-none file:mr-3 file:py-1 file:px-2.5 file:border file:border-white/10 file:rounded-md file:text-xs file:font-semibold file:bg-white/[0.06] file:text-zinc-200 file:hover:bg-white/[0.12] file:cursor-pointer file:transition-colors"
              />
            </div>
            <button
              type="button"
              onClick={() => {
                setCover(null)
                setCoverRemoved(true)
              }}
              title="Remove Cover"
              className="ml-2 mt-4 px-2 py-1 bg-red-500/20 text-red-400 hover:bg-red-500/30 rounded text-xs font-semibold border border-red-500/30 transition-colors cursor-pointer"
            >
              Remove
            </button>
          </div>
        </div>

        {error && <p className="text-sm text-red-400">{error}</p>}

        <div className="flex justify-end space-x-3 pt-4 border-t border-white/10 mt-5">
          <GhostButton type="button" onClick={onClose}>
            Cancel
          </GhostButton>
          <PrimaryButton type="submit" loading={saving}>
            {saving ? 'Saving…' : 'Save Changes'}
          </PrimaryButton>
        </div>
      </form>
    </Modal>
  )
}
