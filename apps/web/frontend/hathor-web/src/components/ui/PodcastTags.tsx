import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type PodcastTag } from '../../api/client'
import Icon from './icons'
import Modal from './Modal'
import { GhostButton, PrimaryButton, TextField } from './fields'

// Tag filter chips (podcasts.html #pod-tags-row): All(n) + Tag(n),
// single-select toggle. Empty (no tags) renders nothing.
export function TagPills({
  tags,
  total,
  activeId,
  onSelect,
}: {
  tags: PodcastTag[]
  total: number
  activeId: number | null
  onSelect: (id: number | null) => void
}) {
  if (tags.length === 0) return null
  const pill = (label: string, active: boolean, onClick: () => void) => (
    <button
      key={label}
      onClick={onClick}
      className={
        active
          ? 'flex-shrink-0 px-4 py-1.5 rounded-full text-xs font-medium bg-orange-500 text-white shadow-lg shadow-orange-500/20 transition-all cursor-pointer'
          : 'flex-shrink-0 px-4 py-1.5 rounded-full text-xs font-medium bg-white/5 border border-white/10 text-zinc-300 hover:text-white hover:bg-white/10 transition-all cursor-pointer'
      }
    >
      {label}
    </button>
  )
  return (
    <div className="flex gap-2 overflow-x-auto pb-3">
      {pill(`All (${total})`, activeId === null, () => onSelect(null))}
      {tags.map((t) =>
        pill(`${t.name} (${t.episodeCount})`, activeId === t.id, () =>
          onSelect(activeId === t.id ? null : t.id),
        ),
      )}
    </div>
  )
}

// Tag manager modal (pod-tags-modal): create + rename + delete with counts.
export function TagManagerModal({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [editingName, setEditingName] = useState('')
  const [deleting, setDeleting] = useState<{ id: number; name: string; count: number } | null>(null)
  const [deletingBusy, setDeletingBusy] = useState(false)
  const { data: tags } = useQuery({ queryKey: ['podcast-tags'], queryFn: api.podcastTags })

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['podcast-tags'] })
    void queryClient.invalidateQueries({ queryKey: ['podcast-tag-map'] })
  }

  const create = async () => {
    const clean = name.trim()
    if (!clean) return
    setError(null)
    setAdding(true)
    try {
      await api.createPodcastTag(clean)
      setName('')
      refresh()
    } catch {
      setError('Name is blank or already used.')
    } finally {
      setAdding(false)
    }
  }

  const startRename = (id: number, current: string) => {
    setEditingId(id)
    setEditingName(current)
    setError(null)
  }

  const commitRename = async () => {
    if (editingId === null) return
    const clean = editingName.trim()
    if (!clean) {
      setError("Name can't be blank.")
      return
    }
    setError(null)
    try {
      await api.renamePodcastTag(editingId, clean)
      setEditingId(null)
      refresh()
    } catch {
      setError('Name is already used.')
    }
  }

  const confirmDelete = async () => {
    if (!deleting) return
    setDeletingBusy(true)
    try {
      await api.deletePodcastTag(deleting.id)
      setDeleting(null)
      refresh()
    } finally {
      setDeletingBusy(false)
    }
  }

  return (
    <Modal open onClose={onClose} title="Podcast tags">
      <div className="bg-white/5 border border-white/10 rounded-xl p-3 mb-4">
        <p className="text-xs font-semibold text-orange-400 tracking-wide mb-2">NEW TAG</p>
        <div className="flex gap-2">
          <TextField
            label="Name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Name…"
            onKeyDown={(e) => {
              if (e.key === 'Enter') void create()
            }}
          />
          <div className="flex items-end">
            <PrimaryButton loading={adding} onClick={() => void create()}>
              Add
            </PrimaryButton>
          </div>
        </div>
      </div>
      {error && <p className="text-sm text-red-400 mb-2">{error}</p>}
      <div className="flex flex-col gap-2 max-h-72 overflow-y-auto">
        {(tags ?? []).length === 0 && (
          <p className="text-xs text-zinc-500">No tags yet — create one above, then assign it from an episode's Tags menu.</p>
        )}
        {(tags ?? []).map((t) => (
          <div key={t.id} className="flex items-center gap-3 bg-white/5 border border-white/10 rounded-xl p-2.5">
            <div className="w-10 h-10 rounded-full bg-orange-500/10 border border-orange-500/20 flex items-center justify-center flex-shrink-0">
              <span className="text-sm font-bold text-orange-400">{(t.name.trim()[0] ?? '?').toUpperCase()}</span>
            </div>
            {editingId === t.id ? (
              <div className="flex-1 min-w-0 flex items-center gap-2">
                <TextField
                  label="Tag name"
                  value={editingName}
                  onChange={(e) => setEditingName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') void commitRename()
                    if (e.key === 'Escape') setEditingId(null)
                  }}
                />
                <PrimaryButton onClick={() => void commitRename()}>Save</PrimaryButton>
              </div>
            ) : (
              <>
                <div className="flex-1 min-w-0">
                  <div className="text-sm font-medium text-zinc-100 truncate">{t.name}</div>
                  <div className="text-xs text-zinc-500">
                    {t.episodeCount} episode{t.episodeCount === 1 ? '' : 's'}
                  </div>
                </div>
                <button
                  title="Rename tag"
                  onClick={() => startRename(t.id, t.name)}
                  className="p-2 rounded-full text-zinc-500 hover:text-white hover:bg-white/10 transition-all cursor-pointer"
                >
                  <Icon name="gear" className="w-4 h-4" />
                </button>
                <button
                  title="Delete tag"
                  onClick={() => setDeleting({ id: t.id, name: t.name, count: t.episodeCount })}
                  className="p-2 rounded-full text-red-400/70 hover:text-red-300 hover:bg-white/10 transition-all cursor-pointer"
                >
                  <Icon name="x" className="w-4 h-4" />
                </button>
              </>
            )}
          </div>
        ))}
      </div>
      <div className="flex justify-end mt-4">
        <GhostButton onClick={onClose}>Close</GhostButton>
      </div>

      <Modal
        open={deleting !== null}
        onClose={() => setDeleting(null)}
        title="Delete tag?"
      >
        <p className="text-sm text-zinc-400">
          <span className="text-zinc-200 font-medium">{deleting?.name}</span> will be removed from{' '}
          {deleting?.count ?? 0} episode{deleting?.count === 1 ? '' : 's'} and from the remote library.
        </p>
        <div className="flex justify-end gap-3 pt-4 mt-4 border-t border-white/10">
          <GhostButton onClick={() => setDeleting(null)}>Cancel</GhostButton>
          <PrimaryButton loading={deletingBusy} onClick={() => void confirmDelete()}>
            {deletingBusy ? 'Deleting…' : 'Yes, delete it'}
          </PrimaryButton>
        </div>
      </Modal>
    </Modal>
  )
}

// Per-episode tag assignment modal (pod-assign-modal): toggle pills + inline create.
export function AssignTagsModal({ file, title, onClose }: { file: string; title: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [adding, setAdding] = useState(false)
  const { data: tags } = useQuery({ queryKey: ['podcast-tags'], queryFn: api.podcastTags })
  const { data: map } = useQuery({ queryKey: ['podcast-tag-map'], queryFn: api.podcastTagMap })

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['podcast-tags'] })
    void queryClient.invalidateQueries({ queryKey: ['podcast-tag-map'] })
  }

  const assigned = new Set(map?.[file] ?? [])
  const toggle = async (id: number, on: boolean) => {
    if (on) await api.unassignPodcastTag(id, file).catch(() => {})
    else await api.assignPodcastTag(id, file).catch(() => {})
    refresh()
  }

  const createAssign = async () => {
    const clean = name.trim()
    if (!clean) return
    setAdding(true)
    try {
      const { id } = await api.createPodcastTag(clean)
      await api.assignPodcastTag(id, file).catch(() => {})
      setName('')
      refresh()
    } catch {
      // blank/duplicate — manager modal explains; stay silent here
    } finally {
      setAdding(false)
    }
  }

  return (
    <Modal open onClose={onClose} title="Tags">
      <p className="text-xs text-zinc-500 truncate mb-4">{title}</p>
      <div className="flex flex-wrap gap-2 mb-4">
        {(tags ?? []).length === 0 && (
          <p className="text-xs text-zinc-500">No tags yet — create one below, it assigns right away.</p>
        )}
        {(tags ?? []).map((t) => {
          const on = assigned.has(t.id)
          return (
            <button
              key={t.id}
              onClick={() => void toggle(t.id, on)}
              className={
                on
                  ? 'px-4 py-1.5 rounded-full text-xs font-medium bg-orange-500 text-white shadow-lg shadow-orange-500/20 transition-all cursor-pointer'
                  : 'px-4 py-1.5 rounded-full text-xs font-medium bg-white/5 border border-white/10 text-zinc-300 hover:text-white hover:bg-white/10 transition-all cursor-pointer'
              }
            >
              {t.name} ({t.episodeCount})
            </button>
          )
        })}
      </div>
      <div className="flex gap-2">
        <TextField
          label="New tag"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="New tag…"
          onKeyDown={(e) => {
            if (e.key === 'Enter') void createAssign()
          }}
        />
        <div className="flex items-end">
          <PrimaryButton loading={adding} onClick={() => void createAssign()}>
            Add
          </PrimaryButton>
        </div>
      </div>
    </Modal>
  )
}
