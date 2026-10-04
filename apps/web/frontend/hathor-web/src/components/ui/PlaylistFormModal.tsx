import { useState, type FormEvent } from 'react'
import { api, type Playlist } from '../../api/client'
import CoverArt from './CoverArt'
import Modal from './Modal'
import { GhostButton, PrimaryButton, TextField } from './fields'

// New + edit playlist dialog (playlist_home.html create card/modal):
// Title + optional description + optional cover image.
export default function PlaylistFormModal({
  playlist,
  onClose,
  onSaved,
}: {
  playlist?: Playlist | null
  onClose: () => void
  onSaved: (playlist: Playlist) => void
}) {
  const editing = playlist ?? null
  const [title, setTitle] = useState(editing?.title ?? '')
  const [description, setDescription] = useState(editing?.description ?? '')
  const [cover, setCover] = useState<string | null>(editing?.thumbnail ?? null)
  const [coverTouched, setCoverTouched] = useState(false)
  const [coverRemoved, setCoverRemoved] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const pickFile = (file: File | undefined) => {
    if (!file) return
    const reader = new FileReader()
    reader.onload = (e) => {
      if (typeof e.target?.result === 'string') {
        setCover(e.target.result)
        setCoverTouched(true)
        setCoverRemoved(false)
      }
    }
    reader.readAsDataURL(file)
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const saved = editing
        ? await api.updatePlaylist(editing.id, {
            title: title.trim(),
            description,
            ...(coverTouched || coverRemoved
              ? { thumbnail: coverRemoved ? 'REMOVE' : cover }
              : {}),
          })
        : await api.createPlaylist(title.trim(), description || null, cover);
      onSaved(saved)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save playlist.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal open onClose={onClose} title={editing ? 'Edit Playlist' : 'New Playlist'}>
      <form onSubmit={submit} className="space-y-4" onClick={(e) => e.stopPropagation()}>
        <TextField label="Title" value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Playlist Name" />
        <div>
          <label htmlFor="playlist-description" className="block text-sm font-semibold text-zinc-300">
            Description
          </label>
          <textarea
            id="playlist-description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Describe your playlist..."
            rows={3}
            className="mt-1 block w-full rounded-lg border border-white/[0.12] shadow-sm focus:border-orange-500 focus:ring-1 focus:ring-orange-500 sm:text-sm px-3 py-2 bg-white/[0.06] text-zinc-100 placeholder-zinc-500 outline-none transition-all duration-300"
          />
        </div>
        <div className="flex items-center gap-4 bg-white/[0.04] p-3 rounded-xl border border-white/[0.08]">
          <CoverArt
            src={coverRemoved ? null : cover}
            alt="Playlist cover"
            className="w-16 h-16 rounded-lg flex-shrink-0"
          />
          <div className="flex-grow flex items-center justify-between">
            <div className="flex-grow">
              <label
                htmlFor="playlist-cover"
                className="block text-xs font-semibold text-zinc-400 uppercase tracking-wider"
              >
                Cover Image
              </label>
              <input
                id="playlist-cover"
                type="file"
                accept="image/*"
                onChange={(e) => pickFile(e.target.files?.[0])}
                className="mt-1 block w-full text-xs text-zinc-300 cursor-pointer bg-transparent outline-none file:mr-3 file:py-1 file:px-2.5 file:border file:border-white/10 file:rounded-md file:text-xs file:font-semibold file:bg-white/[0.06] file:text-zinc-200 file:hover:bg-white/[0.12] file:cursor-pointer file:transition-colors"
              />
            </div>
            {editing && cover && !coverRemoved && (
              <button
                type="button"
                onClick={() => {
                  setCover(null)
                  setCoverTouched(true)
                  setCoverRemoved(true)
                }}
                title="Remove Cover"
                className="ml-2 mt-4 px-2 py-1 bg-red-500/20 text-red-400 hover:bg-red-500/30 rounded text-xs font-semibold border border-red-500/30 transition-colors cursor-pointer"
              >
                Remove
              </button>
            )}
          </div>
        </div>
        {error && <p className="text-sm text-red-400">{error}</p>}
        <div className="flex justify-end gap-3 pt-4 border-t border-white/10">
          <GhostButton type="button" onClick={onClose}>
            Cancel
          </GhostButton>
          <PrimaryButton type="submit" loading={saving} disabled={title.trim().length === 0}>
            {saving ? 'Saving…' : editing ? 'Save Changes' : 'Create Playlist'}
          </PrimaryButton>
        </div>
      </form>
    </Modal>
  )
}
