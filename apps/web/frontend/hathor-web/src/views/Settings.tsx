import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type FfmpegDownloadStatus, type LibraryInfo, type SessionInfo } from '../api/client'
import { engine } from '../audio/engine'
import { useAuth } from '../auth/AuthContext'
import { usePlayer } from '../store/player'
import ToggleSwitch from '../components/ui/ToggleSwitch'
import ViewHeader from '../components/ui/ViewHeader'

// Settings cloned from ui/views/settings.html: folders + rescans, remote
// pull per library, background, download limit, crossfade, system retry.
function Row({
  icon,
  title,
  hint,
  extra,
  children,
}: {
  icon: React.ReactNode
  title: string
  hint: string
  extra?: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 p-4 rounded-xl bg-white/[0.02] border border-white/[0.05] hover:bg-white/[0.04] transition-all duration-300 group">
      <div className="flex items-center gap-4 min-w-0 flex-1">
        <div className="w-11 h-11 rounded-lg bg-white/5 flex items-center justify-center text-zinc-400 group-hover:text-orange-400 transition-colors border border-white/5 shadow-inner shrink-0">
          {icon}
        </div>
        <div className="min-w-0 flex-1">
          <h3 className="text-zinc-200 font-medium">{title}</h3>
          <p className="text-xs text-zinc-500 mt-0.5">{hint}</p>
          {extra}
        </div>
      </div>
      <div className="flex items-center gap-2 shrink-0">{children}</div>
    </div>
  )
}

const btn =
  'px-5 py-2.5 text-sm font-medium text-zinc-200 bg-white/5 hover:bg-orange-500 hover:text-white border border-white/10 hover:border-orange-500 rounded-lg transition-all duration-300 shadow-md cursor-pointer disabled:opacity-50 disabled:cursor-wait inline-flex items-center justify-center gap-2'

function BtnSpinner() {
  return <span className="spinner-btn" aria-hidden="true" />
}

function libStatusLabel(status: string): string {
  return status === 'update-available' ? 'update available' : status.replace(/-/g, ' ')
}

// API errors arrive as raw JSON bodies ({"message":"…"}); surface the message.
function msgOf(e: unknown): string {
  if (!(e instanceof Error)) return 'Failed.'
  try {
    const parsed = JSON.parse(e.message) as { message?: unknown }
    if (typeof parsed.message === 'string' && parsed.message.length > 0) return parsed.message
  } catch {
    // Plain-text error already.
  }
  return e.message
}

// Wallpaper pre-check (mirrors the server's 10 MB cap + image allow-list):
// over-limit bodies get their connection reset mid-upload, which fetch
// reports as an opaque NetworkError instead of the server's 400. Reject
// here so the user gets the real reason. Null = looks fine, upload it.
export const MAX_BACKGROUND_BYTES = 10 * 1024 * 1024
const BACKGROUND_TYPES = ['image/jpeg', 'image/png', 'image/gif', 'image/webp', 'image/bmp']

export function validateBackgroundFile(file: { type: string; size: number }): string | null {
  if (file.type && !BACKGROUND_TYPES.includes(file.type))
    return 'Only JPEG, PNG, GIF, WebP or BMP images are allowed.'
  if (file.size > MAX_BACKGROUND_BYTES) return 'Image must be under 10 MB — pick a smaller file.'
  return null
}

// Upload with upload-specific errors: validation rejects before any
// bytes move, and a killed connection (over-limit body reset mid-stream
// surfaces as a bare fetch TypeError) explains itself instead of printing
// browser internals. Server JSON errors render via msgOf.
export function uploadBackgroundFile(
  file: File,
  upload: (f: File) => Promise<string>,
): Promise<string> {
  const problem = validateBackgroundFile(file)
  if (problem) return Promise.reject(new Error(problem))
  return upload(file).catch((e: unknown) => {
    if (e instanceof TypeError)
      throw new Error('Upload failed — check your connection and try a smaller file.')
    throw new Error(msgOf(e))
  })
}

function formatDate(iso: string): string {  const d = new Date(iso)
  return Number.isNaN(d.getTime())
    ? iso
    : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) +
        ' ' +
        d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}

function SessionRow({
  session,
  busy,
  onRevoke,
}: {
  session: SessionInfo
  busy: boolean
  onRevoke: () => void
}) {
  return (
    <div className="flex items-center gap-3 min-w-0 bg-white/[0.03] border border-white/[0.06] rounded-lg px-3 py-2">
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 min-w-0">
          <span className="text-sm text-zinc-200 truncate">
            {session.deviceLabel ?? 'Unknown device'}
          </span>
          {session.current && (
            <span className="text-[10px] font-semibold uppercase tracking-widest text-orange-400 bg-orange-500/10 rounded-full px-2 py-0.5 flex-shrink-0">
              Current
            </span>
          )}
          {session.rememberMe && (
            <span className="text-[10px] font-semibold uppercase tracking-widest text-zinc-400 bg-white/5 rounded-full px-2 py-0.5 flex-shrink-0">
              30d
            </span>
          )}
        </div>
        <p className="text-xs text-zinc-500 truncate">
          Last used {formatDate(session.lastUsedAtUtc)} · expires {formatDate(session.expiresAtUtc)}
        </p>
      </div>
      <button
        onClick={onRevoke}
        disabled={busy}
        title={session.current ? 'Sign out this device' : 'Revoke this session'}
        className="text-xs font-medium text-zinc-500 hover:text-red-400 transition-colors cursor-pointer disabled:opacity-50 flex-shrink-0"
      >
        {session.current ? 'Sign out' : 'Revoke'}
      </button>
    </div>
  )
}

export default function Settings() {
  const queryClient = useQueryClient()
  const { logout } = useAuth()
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [limit, setLimit] = useState(3)
  const [crossfade, setCrossfade] = useState(false)
  const [seconds, setSeconds] = useState(5)
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const bgRef = useRef<HTMLInputElement>(null)

  const { data: settings } = useQuery({ queryKey: ['settings'], queryFn: api.settings })
  const { data: sessions } = useQuery({ queryKey: ['sessions'], queryFn: api.sessions })
  const { data: ffmpeg, refetch: refetchFfmpeg } = useQuery({
    queryKey: ['ffmpeg'],
    queryFn: api.ffmpegStatus,
  })
  const { data: libBaseline } = useQuery({ queryKey: ['libraries'], queryFn: api.libraryStatus })
  const [libs, setLibs] = useState<LibraryInfo[] | null>(null)
  const [dl, setDl] = useState<FfmpegDownloadStatus | null>(null)
  const dlState = dl?.state

  // Poll the FFmpeg self-install until it lands (ready/failed).
  useEffect(() => {
    if (dlState !== 'downloading' && dlState !== 'extracting') return
    const t = setInterval(() => {
      void api
        .ffmpegDownloadStatus()
        .then((s) => {
          setDl(s)
          if (s.state === 'ready') {
            say('FFmpeg installed — downloads can now transcode to MP3.')
            void refetchFfmpeg()
          } else if (s.state === 'failed') {
            say(`FFmpeg download failed: ${s.error ?? 'unknown error'}`)
          }
        })
        .catch(() => {})
    }, 2000)
    return () => clearInterval(t)
  }, [dlState])

  useEffect(() => {
    if (!settings) return
    setLimit(settings.limitDownloads)
    setCrossfade(settings.crossfadeEnabled)
    setSeconds(settings.crossfadeSeconds)
  }, [settings])

  const say = (msg: string) => {
    setNotice(msg)
    void queryClient.invalidateQueries({ queryKey: ['settings'] })
  }
  const act = (p: Promise<string>) =>
    p.then(say).catch((e: unknown) => say(e instanceof Error ? e.message : 'Failed.'))
  // Tracked async row action: shows a spinner on its button while running.
  const run = (key: string, p: Promise<string>) => {
    setBusy(key)
    return act(p).finally(() => setBusy(null))
  }
  const spin = (key: string) => (busy === key ? <BtnSpinner /> : null)

  const saveCrossfade = () =>
    run(
      'crossfade',
      api
        .setPlayerSettings(crossfade, seconds)
        .then(() => api.updateSettings({ crossfadeEnabled: crossfade, crossfadeSeconds: seconds }))
        .then(() => {
          engine.setCrossfade(crossfade, seconds)
          return `Crossfade ${crossfade ? `on (${seconds}s)` : 'off'}. Gapless handoff always on.`
        }),
    )

  const savePassword = () => {
    if (newPassword !== confirmPassword) {
      say('New passwords do not match.')
      return
    }
    run(
      'password',
      api.changePassword(currentPassword, newPassword).then(() => {
        setCurrentPassword('')
        setNewPassword('')
        setConfirmPassword('')
        return 'Password changed.'
      }),
    )
  }

  return (
    <div className="max-w-4xl mx-auto w-full pt-8 px-6 sm:px-8 pb-24">
      <div className="flex items-center gap-4 mb-8">
        <ViewHeader icon="gear" title="Settings" subtitle="Manage your preferences and configurations" />
      </div>

      {notice && (
        <div className="mb-4 rounded-xl bg-orange-500/10 border border-orange-500/30 text-orange-200 text-sm px-4 py-3">
          {notice}
        </div>
      )}

      <div className="relative z-10 flex flex-col gap-3">
        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z" />
            </svg>
          }
          title="Local Songs Folder"
          hint="Scan the folder into the database, measure loudness for normalization, pull the desktop remote library, or push this library back to the remote."
        >
          <button className={btn} disabled={busy !== null} onClick={() => run('songs', api.scanSongs().then((r) => `Sync complete: ${r.added} added, ${r.updated} updated.`))}>
            {spin('songs')}Scan
          </button>
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() =>
              run('loudness', api.backfillLoudness().then((r) =>
                r.remaining > 0
                  ? `Measured ${r.scanned} songs, ${r.remaining} to go — run again.`
                  : `Loudness measured for ${r.scanned} songs. Library complete.`,
              ))
            }
          >
            {spin('loudness')}Measure
          </button>
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() =>
              run('pull-songs', api.pullSongs().then((r) => {
                void queryClient.invalidateQueries()
                return r.message
              }))
            }
          >
            {spin('pull-songs')}Pull
          </button>
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() => run('push-songs', api.pushSongs().then((r) => r.message))}
          >
            {spin('push-songs')}Push
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 18.75a6 6 0 006-6v-1.5m-6 7.5a6 6 0 01-6-6v-1.5m6 7.5v3.75m-3.75 0h7.5M12 15.75a3 3 0 01-3-3V4.5a3 3 0 116 0v8.25a3 3 0 01-3 3z" />
            </svg>
          }
          title="Local Podcasts Folder"
          hint="Scan the folder into the database, pull remote episodes, or push this library back to the remote."
        >
          <button className={btn} disabled={busy !== null} onClick={() => run('podcasts', api.scanPodcasts().then((r) => `Sync complete: ${r.added} added, ${r.updated} updated.`))}>
            {spin('podcasts')}Scan
          </button>
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() =>
              run('pull-podcasts', api.pullPodcasts().then((r) => {
                void queryClient.invalidateQueries()
                return r.message
              }))
            }
          >
            {spin('pull-podcasts')}Pull
          </button>
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() => run('push-podcasts', api.pushPodcasts().then((r) => r.message))}
          >
            {spin('push-podcasts')}Push
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z" />
            </svg>
          }
          title="Background Image"
          hint="Customize the app's visual appearance"
          extra={
            settings?.backgroundPath ? (
              <p className="text-xs text-orange-400/80 mt-1 font-mono truncate max-w-[240px]">{settings.backgroundPath}</p>
            ) : undefined
          }
        >
          <button
            className="flex items-center gap-2 px-4 py-2.5 text-sm font-medium text-zinc-300 bg-white/5 hover:bg-red-500/20 hover:text-red-400 border border-white/10 hover:border-red-500/50 rounded-lg transition-all duration-300 shadow-md cursor-pointer disabled:opacity-50 disabled:cursor-wait"
            disabled={busy !== null}
            onClick={() => run('bg-remove', api.removeBackground().then(() => 'Background removed.'))}
          >
            {spin('bg-remove')}Remove
          </button>
          <button className={btn} disabled={busy !== null} onClick={() => bgRef.current?.click()}>
            {spin('bg-upload')}Browse
          </button>
          <input
            ref={bgRef}
            type="file"
            accept="image/*"
            className="hidden"
            onChange={(e) => {
              const f = e.target.files?.[0]
              if (f)
                run(
                  'bg-upload',
                  uploadBackgroundFile(f, (file) =>
                    api.uploadBackground(file).then(() => 'Background updated.'),
                  ),
                )
              e.target.value = ''
            }}
          />
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 14l-7 7m0 0l-7-7m7 7V3" />
            </svg>
          }
          title="Download Limit Per Batch"
          hint="Maximum concurrent downloads (1–20)"
        >
          <input
            type="number"
            min={1}
            max={20}
            value={limit}
            onChange={(e) => setLimit(Number(e.target.value))}
            className="w-24 px-3 py-2 text-center text-sm font-medium text-zinc-200 bg-white/5 border border-white/10 outline-none focus:border-orange-500 rounded-lg"
          />
          <button className={btn} disabled={busy !== null} onClick={() => run('limit', api.updateSettings({ limitDownloads: limit }).then(() => `Download limit updated to ${limit}.`))}>
            {spin('limit')}Save
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zM9 10l12-3" />
            </svg>
          }
          title="Crossfade"
          hint="Overlap the end of each song into the next (1–12s). Gapless handoff is always on."
          extra={
            <div className="flex items-center gap-3 mt-2">
              <span className="flex items-center gap-2 text-xs text-zinc-400 select-none">
                <ToggleSwitch checked={crossfade} onChange={setCrossfade} label="Enable crossfade" />
                Enabled
              </span>
              <input
                type="range"
                min={1}
                max={12}
                step={1}
                value={seconds}
                onChange={(e) => setSeconds(Number(e.target.value))}
                style={{ '--range-percent': `${Math.round(((seconds - 1) / 11) * 100)}%` } as React.CSSProperties}
                className="w-32 sm:w-40 cursor-pointer outline-none"
              />
              <span className="text-xs text-orange-400/80 font-mono w-8">{seconds}s</span>
            </div>
          }
        >
          <button className={btn} disabled={busy !== null} onClick={saveCrossfade}>
            {spin('crossfade')}Save
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 3v18m0-18c-4 0-7 2.5-7 7v4c0 4.5 3 7 7 7s7-2.5 7-7v-4c0-4.5-3-7-7-7zm-7 10h14" />
            </svg>
          }
          title="Normalize volume"
          hint="Even out loud and quiet tracks (all music and podcasts). Measured per-track loudness when analyzed, leveling otherwise. This browser only."
          extra={
            <div className="flex items-center gap-3 mt-2">
              <span className="flex items-center gap-2 text-xs text-zinc-400 select-none">
                <ToggleSwitch
                  checked={usePlayer((s) => s.normalize)}
                  onChange={(v) => usePlayer.getState().setNormalize(v)}
                  label="Normalize volume"
                />
                Enabled
              </span>
            </div>
          }
        >
          <span className="text-xs text-zinc-600 uppercase tracking-widest">Instant · no save needed</span>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          }
          title="Auto-skip podcast chapters"
          hint="Timestamped episodes start at the first chapter and jump to the next chapter at each end time. Applies automatically to every episode with chapters."
          extra={
            <div className="flex items-center gap-3 mt-2">
              <span className="flex items-center gap-2 text-xs text-zinc-400 select-none">
                <ToggleSwitch
                  checked={usePlayer((s) => s.chapterSkip)}
                  onChange={(v) => usePlayer.getState().setChapterSkip(v)}
                  label="Auto-skip podcast chapters"
                />
                Enabled
              </span>
            </div>
          }
        >
          <span className="text-xs text-zinc-600 uppercase tracking-widest">Instant · saved to your account</span>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M15.75 5.25a3 3 0 013 3m3 0a6 6 0 01-7.029 5.912c-.563-.097-1.159.026-1.563.43L10.5 17.25H8.25v2.25H6v2.25H2.25v-2.818c0-.597.237-1.17.659-1.591l6.499-6.499c.404-.404.527-1 .43-1.563A6 6 0 1121.75 8.25z" />
            </svg>
          }
          title="Change Password"
          hint="Confirm your current password, then choose a new one (8+ characters)"
          extra={
            <div className="flex flex-col sm:flex-row gap-2 mt-2 w-full">
              <input
                type="password"
                value={currentPassword}
                onChange={(e) => setCurrentPassword(e.target.value)}
                placeholder="Current password"
                autoComplete="current-password"
                className="flex-1 min-w-0 px-3 py-2 text-sm text-zinc-200 bg-white/5 border border-white/10 outline-none focus:border-orange-500 rounded-lg placeholder:text-zinc-600"
              />
              <input
                type="password"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                placeholder="New password"
                autoComplete="new-password"
                className="flex-1 min-w-0 px-3 py-2 text-sm text-zinc-200 bg-white/5 border border-white/10 outline-none focus:border-orange-500 rounded-lg placeholder:text-zinc-600"
              />
              <input
                type="password"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                placeholder="Repeat new password"
                autoComplete="new-password"
                className="flex-1 min-w-0 px-3 py-2 text-sm text-zinc-200 bg-white/5 border border-white/10 outline-none focus:border-orange-500 rounded-lg placeholder:text-zinc-600"
              />
            </div>
          }
        >
          <button
            className={btn}
            disabled={busy !== null || !currentPassword || !newPassword}
            onClick={savePassword}
          >
            {spin('password')}Save
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M15 7h3a2 2 0 012 2v8a2 2 0 01-2 2h-3M5 12h12m0 0l-3-3m3 3l-3 3" />
            </svg>
          }
          title="Active Sessions"
          hint="Devices logged into this account (24h sessions, 30d when remembered)"
          extra={
            <div className="flex flex-col gap-2 mt-2 w-full min-w-0">
              {(sessions ?? []).map((s) => (
                <SessionRow
                  key={s.id}
                  session={s}
                  busy={busy !== null}
                  onRevoke={() => {
                    if (s.current) {
                      setBusy('session')
                      void logout().finally(() => setBusy(null))
                      return
                    }
                    run(
                      'session',
                      api.revokeSession(s.id).then(() => {
                        void queryClient.invalidateQueries({ queryKey: ['sessions'] })
                        return 'Session revoked.'
                      }),
                    )
                  }}
                />
              ))}
              {(sessions ?? []).length === 0 && (
                <p className="text-xs text-zinc-500">No active sessions.</p>
              )}
            </div>
          }
        >
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() =>
              run(
                'sessions-all',
                api.logoutEverywhere().then(() => {
                  void queryClient.invalidateQueries({ queryKey: ['sessions'] })
                  return 'Logged out everywhere. This device is signed out too — please sign in again.'
                }),
              )
            }
          >
            {spin('sessions-all')}Log out everywhere
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 14l-7 7m0 0l-7-7m7 7V3" />
            </svg>
          }
          title="FFmpeg"
          hint={
            dlState === 'downloading'
              ? `Downloading… ${dl && dl.progress >= 0 ? `${Math.round(dl.progress * 100)}%` : ''}`
              : dlState === 'extracting'
                ? 'Extracting…'
                : dlState === 'ready'
                  ? 'Installed and verified.'
                  : dlState === 'failed'
                    ? `Download failed: ${dl?.error ?? 'unknown error'}`
                    : ffmpeg
                      ? ffmpeg.found
                        ? `Ready (${ffmpeg.exe})`
                        : 'Not found — downloads cannot transcode without it.'
                      : 'Checking…'
          }
          extra={
            (dlState === 'downloading' || dlState === 'extracting') && dl && dl.progress >= 0 ? (
              <div className="mt-2 h-1.5 w-48 rounded-full bg-white/10 overflow-hidden">
                <div
                  className="h-full bg-orange-500 rounded-full transition-all duration-500"
                  style={{ width: `${Math.round(dl.progress * 100)}%` }}
                />
              </div>
            ) : undefined
          }
        >
          <button
            className={btn}
            disabled={busy !== null || dlState === 'downloading' || dlState === 'extracting'}
            onClick={() => {
              const key = 'ffmpeg-dl'
              setBusy(key)
              void api
                .startFfmpegDownload()
                .then((s) => {
                  setDl(s)
                  if (s.state === 'ready') {
                    say(s.exe ? 'FFmpeg is already installed.' : 'FFmpeg is ready.')
                    void refetchFfmpeg()
                  }
                })
                .catch((e: unknown) => say(msgOf(e)))
                .finally(() => setBusy(null))
            }}
          >
            {spin('ffmpeg-dl')}Download
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M15 10l4.55-2.28A1 1 0 0121 8.62v6.76a1 1 0 01-1.45.9L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z" />
            </svg>
          }
          title="YouTube Downloader"
          hint="The searcher/downloader is a compiled library — a check reports the latest release; applying it means updating the package and redeploying."
          extra={
            <div className="flex flex-col gap-1 mt-1">
              {(libs ?? libBaseline ?? []).map((l) => (
                <p key={l.package} className="text-xs text-zinc-500 font-mono">
                  {l.package} {l.current ?? '?'} → {l.latest ?? '?'} · {libStatusLabel(l.status)}
                </p>
              ))}
            </div>
          }
        >
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() =>
              run(
                'libs-check',
                api
                  .checkLibraryUpdates()
                  .then((r) => {
                    setLibs(r)
                    const yt = r.find((l) => l.package === 'YoutubeExplode')
                    return yt
                      ? `YouTube downloader: ${yt.current ?? '?'} → ${yt.latest ?? '?'} (${libStatusLabel(yt.status)}).`
                      : 'Library check finished.'
                  }),
              )
            }
          >
            {spin('libs-check')}Check for updates
          </button>
        </Row>

        <Row
          icon={
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
          }
          title="System Status"
          hint={ffmpeg ? (ffmpeg.found ? `FFmpeg ready (${ffmpeg.exe})` : 'FFmpeg missing') : 'Checking…'}
        >
          <button
            className={btn}
            disabled={busy !== null}
            onClick={() => {
              const key = 'maintenance'
              setBusy(key)
              void api
                .runMaintenance()
                .then((r) => {
                  say(r.allOk ? 'All set — everything is up to date.' : 'Startup check finished — see details.');
                  void refetchFfmpeg();
                })
                .catch(() => say('Startup check failed.'))
                .finally(() => setBusy(null))
            }}
          >
            {spin('maintenance')}Retry
          </button>
        </Row>
      </div>
    </div>
  )
}
