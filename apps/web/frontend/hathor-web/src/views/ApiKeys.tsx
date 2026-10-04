import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type CreatedApiKey } from '../api/client'
import Icon from '../components/ui/icons'
import Modal from '../components/ui/Modal'
import ViewHeader from '../components/ui/ViewHeader'
import { GhostButton, PrimaryButton } from '../components/ui/fields'
import { EmptyState, LoadingState } from '../components/ui/states'

// API keys page: mint headless tokens (Stream Deck, Home Assistant, curl)
// with a scope subset. The raw token is shown exactly once — copy it now.
const ALL_SCOPES = [
  { value: 'player:read', label: 'Read playback', hint: 'state, queue, now playing' },
  { value: 'player:control', label: 'Full control', hint: 'playback + everything below' },
  { value: 'library:read', label: 'Read library', hint: 'songs, podcasts, playlists, search' },
  { value: 'downloads:write', label: 'Downloads', hint: 'submit, retry, cancel + poll status' },
  { value: 'playlists:write', label: 'Playlists', hint: 'create, edit, add/remove songs, tags + list' },
  { value: 'library:write', label: 'Library edits', hint: 'metadata, delete, scans, sync, settings + read' },
] as const

function formatDate(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime())
    ? iso
    : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
}

export default function ApiKeys() {
  const queryClient = useQueryClient()
  const [name, setName] = useState('')
  const [scopes, setScopes] = useState<string[]>([...ALL_SCOPES.map((s) => s.value)])
  const [created, setCreated] = useState<CreatedApiKey | null>(null)
  const [copied, setCopied] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [revoking, setRevoking] = useState<{ id: string; name: string } | null>(null)
  const [revokeBusy, setRevokeBusy] = useState(false)

  const { data: keys, isLoading } = useQuery({ queryKey: ['api-keys'], queryFn: api.apiKeys })
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['api-keys'] })

  const toggleScope = (value: string) =>
    setScopes((prev) => (prev.includes(value) ? prev.filter((s) => s !== value) : [...prev, value]))

  const create = () => {
    if (!name.trim() || scopes.length === 0 || busy) return
    setBusy(true)
    setError(null)
    api
      .createApiKey(name.trim(), scopes)
      .then((k) => {
        setCreated(k)
        setCopied(false)
        setName('')
        refresh()
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Failed to create key.'))
      .finally(() => setBusy(false))
  }

  const revoke = (id: string) => {
    setRevokeBusy(true)
    api
      .revokeApiKey(id)
      .then(() => {
        setRevoking(null)
        refresh()
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Failed to revoke key.'))
      .finally(() => setRevokeBusy(false))
  }

  const copy = () => {
    if (!created) return
    void navigator.clipboard
      .writeText(created.token)
      .then(() => setCopied(true))
      .catch(() => setCopied(false))
  }

  return (
    <div className="flex flex-col w-full h-full">
      <div className="max-w-5xl mx-auto w-full px-6 sm:px-8 pt-6">
        <ViewHeader
          icon="key"
          compact
          title="API Keys"
          subtitle="Tokens for headless clients — curl, Stream Deck, Home Assistant"
        />
      </div>

      <div className="max-w-5xl w-full mx-auto px-6 sm:px-8 pb-10 pt-6 flex flex-col gap-4">
        {created && (
          <div className="rounded-2xl bg-orange-500/10 border border-orange-500/30 p-4 flex flex-col gap-3">
            <p className="text-sm text-orange-200 font-medium">
              Copy this token now — it will never be shown again.
            </p>
            <div className="flex items-center gap-2">
              <code className="flex-1 min-w-0 truncate font-mono text-sm text-zinc-100 bg-black/40 rounded-lg px-3 py-2">
                {created.token}
              </code>
              <button
                onClick={copy}
                className="px-4 py-2 rounded-lg bg-orange-500 hover:bg-orange-400 text-white text-sm font-medium transition-colors cursor-pointer flex-shrink-0"
              >
                {copied ? 'Copied' : 'Copy'}
              </button>
              <button
                onClick={() => setCreated(null)}
                title="Dismiss"
                className="p-2 rounded-full text-zinc-400 hover:text-white hover:bg-white/10 cursor-pointer flex-shrink-0"
              >
                <Icon name="x" className="w-5 h-5" />
              </button>
            </div>
            <p className="text-xs text-zinc-400 font-mono truncate">
              Example: curl -H "Authorization: Bearer {created.prefix}…" http://server:port/api/v1/player/state
            </p>
          </div>
        )}

        <div className="rounded-2xl bg-black/20 backdrop-blur-2xl border border-white/5 p-4 sm:p-5 flex flex-col gap-4">
          <h3 className="text-zinc-100 font-medium">New key</h3>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Name — e.g. Stream Deck"
            maxLength={64}
            className="w-full px-3 py-2.5 text-sm text-zinc-200 bg-white/5 border border-white/10 outline-none focus:border-orange-500 rounded-lg placeholder:text-zinc-600"
          />
          <div className="grid sm:grid-cols-2 xl:grid-cols-3 gap-2">
            {ALL_SCOPES.map((s) => (
              <label
                key={s.value}
                className={`flex-1 flex items-start gap-2 rounded-xl border px-3 py-2.5 cursor-pointer transition-colors ${
                  scopes.includes(s.value)
                    ? 'border-orange-500/50 bg-orange-500/10'
                    : 'border-white/10 bg-white/[0.02] hover:bg-white/[0.05]'
                }`}
              >
                <input
                  type="checkbox"
                  checked={scopes.includes(s.value)}
                  onChange={() => toggleScope(s.value)}
                  className="w-4 h-4 mt-0.5 rounded accent-orange-500 cursor-pointer flex-shrink-0"
                />
                <span className="min-w-0">
                  <span className="block text-sm text-zinc-200 font-mono truncate">{s.value}</span>
                  <span className="block text-xs text-zinc-500">
                    {s.label} — {s.hint}
                  </span>
                </span>
              </label>
            ))}
          </div>
          {error && <p className="text-sm text-red-400">{error}</p>}
          <div>
            <button
              onClick={create}
              disabled={!name.trim() || scopes.length === 0 || busy}
              className="px-5 py-2.5 text-sm font-medium text-zinc-200 bg-white/5 hover:bg-orange-500 hover:text-white border border-white/10 hover:border-orange-500 rounded-lg transition-all shadow-md cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed inline-flex items-center gap-2"
            >
              {busy && <span className="spinner-btn" aria-hidden="true" />}
              Generate token
            </button>
          </div>
        </div>

        <div className="rounded-2xl bg-black/20 backdrop-blur-2xl border border-white/5 p-2 sm:p-3 flex flex-col divide-y divide-white/[0.06]">
          {isLoading ? (
            <LoadingState label="Loading keys…" />
          ) : (keys ?? []).length === 0 ? (
            <EmptyState title="No API keys yet" hint="Generate one above for external access." />
          ) : (
            keys!.map((k) => (
              <div key={k.id} className="flex items-center gap-3 p-3">
                <div className="flex-1 min-w-0">
                  <div className="text-sm font-medium text-zinc-100 truncate">{k.name}</div>
                  <div className="text-xs text-zinc-500 font-mono truncate">
                    hth_{k.prefix}… · {k.scopes} · {formatDate(k.createdAtUtc)}
                  </div>
                </div>
                <button
                  onClick={() => setRevoking({ id: k.id, name: k.name })}
                  title="Revoke key"
                  className="text-xs font-medium text-zinc-500 hover:text-red-400 transition-colors cursor-pointer flex-shrink-0"
                >
                  Revoke
                </button>
              </div>
            ))
          )}
        </div>
      </div>

      <Modal open={revoking !== null} onClose={() => !revokeBusy && setRevoking(null)} title="Revoke API key?">
        <p className="text-sm text-zinc-400">
          <span className="text-zinc-200 font-medium">{revoking?.name}</span> will stop working immediately.
          Clients using it will need a new token.
        </p>
        <div className="flex justify-end gap-3 pt-4 mt-4 border-t border-white/10">
          <GhostButton onClick={() => setRevoking(null)} disabled={revokeBusy}>
            Cancel
          </GhostButton>
          <PrimaryButton loading={revokeBusy} onClick={() => revoking && revoke(revoking.id)}>
            {revokeBusy ? 'Revoking…' : 'Yes, revoke it'}
          </PrimaryButton>
        </div>
      </Modal>
    </div>
  )
}
