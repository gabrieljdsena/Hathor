// Typed REST client for /api/v1. Mirrors the plan §4 endpoint surface.
// The web UI is one client among many (curl, mobile, Stream Deck).

const BASE = '/api/v1'

export interface Song {
  file: string
  artist: string
  title: string
  album: string
  year: string
  duration: number
  coverArt: string | null
  dateDownload: string | null
  isPodcast?: boolean
  loudnessDb?: number | null
}

export interface QueueSource {
  type: string
  id: string | null
}

export interface Playlist {
  id: number
  title: string
  description: string | null
  thumbnail: string | null
}

export interface PlaylistWithCount extends Playlist {
  songCount: number
}

export interface PodcastTag {
  id: number
  name: string
  episodeCount: number
}

export interface PodcastTimestamp {
  id: number
  podcastFile: string
  name: string
  startSecs: number
  endSecs: number | null
}

export interface UserSettings {
  volume: number
  limitDownloads: number
  backgroundPath: string | null
  crossfadeEnabled: boolean
  crossfadeSeconds: number
  lastRoute: string | null
  browser: string | null
  chapterSkip: boolean
}

export interface PlayerSettings {
  crossfadeEnabled: boolean
  crossfadeSeconds: number
}

export interface SystemStatus {
  found: boolean
  exe: string | null
  dir: string | null
  error: string | null
}

export interface LibraryInfo {
  package: string
  current: string | null
  latest: string | null
  status: string
}

export interface ITunesHit {
  title: string
  artist: string
  album: string
  year: string
  genre: string
  artworkUrl: string
}

export interface Artwork {
  url: string
}

export interface FfmpegDownloadStatus {
  state: 'idle' | 'downloading' | 'extracting' | 'ready' | 'failed'
  progress: number
  exe: string | null
  error: string | null
}

// YouTube bot-check protection: provisionState reuses the installer
// vocabulary plus building|starting while the token provider is set up.
export interface YoutubeProtectionStatus {
  enabled: boolean
  provisionState: string
  provisionProgress: number
  nodeFound: boolean
  nodeVersion: string | null
  pluginReady: boolean
  providerRunning: boolean
  providerPort: number
  version: string | null
  pluginVerified: boolean
  error: string | null
}

export interface ApiKey {
  id: string
  name: string
  prefix: string
  scopes: string
  createdAtUtc: string
}

export interface CreatedApiKey extends ApiKey {
  token: string
}

export interface MetadataPatch {
  title: string | null
  artist: string | null
  album: string | null
  year: string | null
  genre: string | null
  coverArt: string | null // data: URL | http(s) URL | 'REMOVE' | null(keep)
}

// Deferred metadata edit: PATCH on the playing file stashes the payload
// (pending=true, 202) instead of rewriting tags mid-stream; it applies
// automatically on track change.
export interface PendingEdit {
  file: string
  isPodcast: boolean
  title: string | null
  artist: string | null
  album: string | null
  year: string | null
  genre: string | null
  coverArt: string | null
  createdUtc: string
}

// Stable per-browser device id (one UUID in localStorage, shared by all
// tabs): readers exclude their own id, so a browser never echoes its own
// paused track back as a resume pill. crypto.randomUUID needs a secure
// context (missing on plain-LAN http) — fall back to a random string.
const DEVICE_KEY = 'hathor:device-id'
export function deviceId(): string {
  try {
    let id = localStorage.getItem(DEVICE_KEY)
    if (!id) {
      id =
        globalThis.crypto?.randomUUID?.() ??
        `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`
      localStorage.setItem(DEVICE_KEY, id)
    }
    return id
  } catch {
    return 'ephemeral'
  }
}

// Short human label for the pill ("from Windows"); the server falls back
// to the User-Agent substring when this is null.
export function deviceLabel(): string | null {
  try {
    const nav = navigator as unknown as {
      userAgentData?: { platform?: string }
      platform?: string
    }
    return nav.userAgentData?.platform || nav.platform || null
  } catch {
    return null
  }
}

// Resume-across-devices spot: another device's paused playback state.
// userKey is the writer's device id. Pull only surfaces it — tapping plays
// explicitly, nothing auto-plays. queueFiles is the writer's Up Next
// snapshot (current track first); queueSource preserves its context.
// Older spots omit both — resume falls back to the single file.
export interface PlaybackSpot {
  userKey: string
  file: string
  positionSec: number
  isPodcast: boolean
  device: string | null
  updatedUtc: string
  queueFiles?: string[] | null
  queueSource?: QueueSource | null
}

export interface HistoryItem {
  song: Song
  datePlayed: string | null
  dateDownload: string | null
  downloadedLink: string | null
}

export interface PlayedPlaylistItem {
  playlist: Playlist
  datePlayed: string | null
}

export interface PagedResult<T> {
  items: T[]
  totalPages: number
  currentPage: number
}

export interface DailyMix {
  date: string
  songs: Song[]
  cached: boolean
}

export type DiscoverSource =
  | 'artist'
  | 'chart'
  | 'llm'
  | 'similar'
  | 'cf'
  | 'graph'
  | 'tag'
  | 'audio'
  | 'popular'

export interface DiscoverItem {
  title: string
  artist: string
  album: string
  year: string
  genre: string
  artworkUrl: string
  source: DiscoverSource
  score: number
  mbid?: string | null
  reason?: string | null
  seedArtist?: string | null
}

export interface Discover {
  date: string
  items: DiscoverItem[]
  cached: boolean
}

export interface DiscoverSourceInsight {
  source: string
  downloads: number
  dismissals: number
  previews: number
  clicks: number
  conversionRate: number
  learnedPrior: number
}

export interface DiscoverInsights {
  windowDays: number
  downloads: number
  dismissals: number
  previews: number
  clicks: number
  conversionRate: number
  completedPlays: number
  skippedPlays: number
  playbackCompletionRate: number
  uniqueArtists: number
  sources: DiscoverSourceInsight[]
}

export interface DiscoverTuningWeights {
  similarity: number
  tag: number
  affinity: number
  popularity: number
  source: number
  audio: number
}

export interface DiscoverTuning {
  applied: boolean
  insufficientData: boolean
  preset: string | null
  snapshots: number
  labeled: number
  currentScore: number
  bestScore: number
  weights: DiscoverTuningWeights | null
}

export type DiscoverFeedbackEvent = 'dismiss' | 'preview' | 'click' | 'download'

export interface DiscoverFeedback {
  title: string
  artist: string
  mbid?: string | null
  source: DiscoverSource
  event: DiscoverFeedbackEvent
  seedArtist?: string | null
  score?: number | null
}

export interface VideoHit {
  id: string
  title: string
  uploader: string
  durationSec: number
  thumbnail: string
}

export interface DownloadJob {
  qid: string
  url: string | null
  title: string
  artist: string | null
  status: string
  progress: number
  error: string | null
  filename: string | null
  isPodcast: boolean
}

export interface Lyrics {
  synced: string | null
  plain: string | null
}

export interface LyricsHit {
  id: number
  trackName: string
  artistName: string
  albumName: string | null
  duration: number | null
  syncedLyrics: string | null
  plainLyrics: string | null
}

export interface PlaybackEventPayload {
  segmentId: string
  file: string
  positionSec: number
  durationSec: number
  completed: boolean
  skipped: boolean
  startedAtUtc: string
}

export interface PlayerState {
  currentSong: Song | null
  isPlaying: boolean
  positionSec: number
  volume: number
  shuffle: boolean
  repeat: boolean
  queue: Song[]
  source: QueueSource | null
  isCustomQueue: boolean
  firstPlay: boolean
  queueTotal: number
  // Monotonic per user, bumped on every queue-structure change. Tabs skip
  // re-retaining the queue array when it matches the held version.
  queueVersion: number
}

export interface AiDjSettings {
  batchSize: number
  commentaryMode: 'auto' | 'always' | 'off'
  language: 'auto' | 'pt-BR' | 'en-US'
  discoveryLevel: number
  brief: string
  preferredGenres: string
  avoidArtists: string
  duckLevel: number
  voiceLeadSeconds: number
}

export interface AiDjBatch {
  id: string
  number: number
  status: string
  tracks: Song[]
  outroText: string | null
  outroClipIds: string[]
  outroDurationSec: number
  introText: string | null
  introClipIds: string[]
  introDurationSec: number
  shouldComment: boolean
  mood: string | null
  reason: string | null
}

export interface AiDjState {
  enabled: boolean
  configured: boolean
  sessionId: string | null
  status: string
  settings: AiDjSettings
  currentBatch: AiDjBatch | null
  nextBatch: AiDjBatch | null
  error: string | null
}

export interface AiDjStartPayload {
  batchSize?: number | null
  commentaryMode?: string | null
  language?: string | null
  discoveryLevel?: number | null
  brief?: string | null
  preferredGenres?: string | null
  avoidArtists?: string | null
}

function authHeaders(): HeadersInit {
  const token = localStorage.getItem('hathor:token')
  return token ? { Authorization: `Bearer ${token}` } : {}
}

// Endpoints that are legitimately allowed to answer 401 without a session
// (bad credentials, malformed refresh token). Every other auth endpoint
// (/auth/me, /sessions, /logout, logout-all, change-password) needs the
// same silent-refresh path as the rest of the API.
const ANONYMOUS_AUTH_PATHS = ['/auth/login', '/auth/register', '/auth/refresh', '/auth/status']

function isAnonymousAuthPath(path: string): boolean {
  return ANONYMOUS_AUTH_PATHS.some((p) => path === p || path.startsWith(`${p}?`))
}

// Single-flight silent refresh: on 401, rotate via the stored refresh token
// once, then retry the request. Returns 'rejected' only when the server
// explicitly invalidated the token; network/5xx/429 keep the session so a
// temporary outage never logs the user out.
type RefreshResult = 'ok' | 'rejected' | 'unavailable'
let refreshFlight: Promise<RefreshResult> | null = null

async function postRefresh(refreshToken: string): Promise<RefreshResult> {
  try {
    const res = await fetch(`${BASE}/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
    if (res.status === 400 || res.status === 401 || res.status === 403) return 'rejected'
    if (!res.ok) return 'unavailable'
    const tokens = (await res.json()) as { accessToken: string; refreshToken: string }
    localStorage.setItem('hathor:token', tokens.accessToken)
    localStorage.setItem('hathor:refresh', tokens.refreshToken)
    return 'ok'
  } catch {
    return 'unavailable'
  }
}

function refreshTokens(): Promise<RefreshResult> {
  refreshFlight ??= (async (): Promise<RefreshResult> => {
    try {
      const started = localStorage.getItem('hathor:refresh')
      if (!started) return 'rejected'
      const rotate = async (): Promise<RefreshResult> => {
        const current = localStorage.getItem('hathor:refresh')
        if (!current) return 'rejected'
        // Another tab rotated while this one waited for the lock: its new
        // access token is already in shared storage, so reuse it instead of
        // replaying the old refresh token (which would trip reuse detection
        // and revoke every session).
        if (current !== started) return 'ok'
        return postRefresh(current)
      }
      return navigator.locks ? await navigator.locks.request('hathor:refresh', rotate) : await rotate()
    } finally {
      refreshFlight = null
    }
  })()
  return refreshFlight
}

// JWT exp check for media URLs: streams bypass request(), so a 15-minute
// access token baked into an <audio> URL goes stale with no 401-refresh path.
export function tokenExpiresWithin(token: string, seconds: number): boolean {
  try {
    const payload = token.split('.')[1]
    if (!payload) return true
    const padded = payload + '='.repeat((4 - (payload.length % 4)) % 4)
    const json = JSON.parse(atob(padded.replace(/-/g, '+').replace(/_/g, '/'))) as { exp?: unknown }
    return typeof json.exp !== 'number' || json.exp * 1000 <= Date.now() + seconds * 1000
  } catch {
    return true
  }
}

// Token for transports that bypass request() (media URLs, SignalR
// reconnects). Refreshes when close to expiry; on a transient refresh
// failure it still returns the current token so callers can retry instead
// of killing playback/session.
export async function authToken(): Promise<string> {
  let token = ''
  try {
    token = localStorage.getItem('hathor:token') ?? ''
  } catch {
    return ''
  }
  if (!token || !tokenExpiresWithin(token, 60)) return token
  if ((await refreshTokens()) === 'ok') {
    try {
      return localStorage.getItem('hathor:token') ?? token
    } catch {
      return token
    }
  }
  return token
}

function logoutLocal() {
  localStorage.removeItem('hathor:token')
  localStorage.removeItem('hathor:refresh')
  localStorage.removeItem('hathor:username')
  if (!location.pathname.startsWith('/login')) location.href = '/login'
}

async function request<T>(path: string, init?: RequestInit, retry = true): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...init?.headers },
  })
  if (res.status === 401 && retry && !isAnonymousAuthPath(path)) {
    const result = await refreshTokens()
    if (result === 'ok') return request<T>(path, init, false)
    if (result === 'rejected') {
      logoutLocal()
      throw new Error('Unauthorized')
    }
    // Transient refresh failure: keep the stored session and let the caller
    // retry once the server is reachable again.
    throw new Error('Session refresh unavailable — check your connection and retry.')
  }
  if (res.status === 401) {
    if (!isAnonymousAuthPath(path)) {
      logoutLocal()
      throw new Error('Unauthorized')
    }
    const body = await res.text().catch(() => '')
    throw new Error(body || 'Unauthorized')
  }
  if (res.status === 429) {
    const retryAfter = res.headers.get('Retry-After')
    const hint = retryAfter ? ` (retry in ${retryAfter}s)` : ' (too many requests — wait a few seconds and retry)'
    const body = await res.text().catch(() => '')
    throw new Error((body && `${body}${hint}`) || `Rate limited: 429${hint}`)
  }
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Request failed: ${res.status}`)
  }
  if (res.status === 204) return undefined as T
  // Some endpoints answer 200 with an empty body (e.g. lyrics save):
  // only parse when there is actually something to parse.
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export interface SessionInfo {
  id: string
  rememberMe: boolean
  deviceLabel: string | null
  ipAddress: string | null
  createdAtUtc: string
  expiresAtUtc: string
  lastUsedAtUtc: string
  current: boolean
}

export const api = {
  // auth (rememberMe: 24h session by default, 30d when marked)
  login: (username: string, password: string, rememberMe = false) =>
    request<{ accessToken: string; refreshToken: string; username: string }>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, password, rememberMe }),
    }),
  register: (username: string, password: string, rememberMe = false) =>
    request<{ accessToken: string; refreshToken: string; username: string }>('/auth/register', {
      method: 'POST',
      body: JSON.stringify({ username, password, rememberMe }),
    }),
  me: () => request<{ userId: string; username: string }>('/auth/me'),
  authStatus: () => request<{ registrationOpen: boolean }>('/auth/status'),
  logout: () => {
    const refreshToken = localStorage.getItem('hathor:refresh')
    return request<void>('/auth/logout', {
      method: 'POST',
      body: JSON.stringify({ refreshToken }),
    }).catch(() => {})
  },
  logoutEverywhere: () => request<void>('/auth/logout-all', { method: 'POST' }),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('/auth/change-password', {
      method: 'POST',
      body: JSON.stringify({ CurrentPassword: currentPassword, NewPassword: newPassword }),
    }),
  sessions: () => request<SessionInfo[]>('/auth/sessions'),
  revokeSession: (id: string) => request<void>(`/auth/sessions/${id}`, { method: 'DELETE' }),

  // Personal access tokens for headless clients (raw token shown once).
  apiKeys: () => request<ApiKey[]>('/ApiKeys'),
  createApiKey: (name: string, scopes: string[]) =>
    request<CreatedApiKey>('/ApiKeys', {
      method: 'POST',
      body: JSON.stringify({ Name: name, Scopes: scopes }),
    }),
  revokeApiKey: (id: string) => request<void>(`/ApiKeys/${id}`, { method: 'DELETE' }),

  // library (paged: omit page/pageSize for the legacy whole list)
  songs: (params: { search?: string; sort?: string; dir?: string; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.sort) q.set('sort', params.sort)
    if (params.dir) q.set('dir', params.dir)
    if (params.page !== undefined) q.set('page', String(params.page))
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize))
    return request<Song[]>(`/songs?${q}`)
  },
  songCount: (search?: string) =>
    request<number>(`/songs/count${search ? `?search=${encodeURIComponent(search)}` : ''}`),
  // Ordered file names for queue building (same search/sort as songs,
  // capped server-side): play-all without holding full Song[] lists.
  songsFiles: (params: { search?: string; sort?: string; dir?: string } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.sort) q.set('sort', params.sort)
    if (params.dir) q.set('dir', params.dir)
    return request<string[]>(`/songs/files?${q}`)
  },

  // player state + headless controls (plan §4 — same surface external clients use)
  playerState: () => request<PlayerState>('/player/state'),
  // Playback segment telemetry (completion/skip quality for Discover).
  // keepalive lets the pagehide flush survive a tab close.
  recordPlaybackEvent: (event: PlaybackEventPayload, keepalive = false) =>
    request<void>('/player/playback-events', {
      method: 'POST',
      body: JSON.stringify(event),
      keepalive,
    }),
  play: (file?: string, isPodcast?: boolean, instanceId?: string | null) =>
    request<PlayerState>('/player/play', {
      method: 'POST',
      body: JSON.stringify({
        file: file ?? null,
        isPodcast: isPodcast ?? null,
        instanceId: instanceId ?? null,
      }),
    }),
  setQueue: (currentFile: string | null, files: Song[], source: QueueSource | null, playlistId: number | null = null) =>
    request<PlayerState>('/player/queue', {
      method: 'PUT',
      body: JSON.stringify({
        currentFile,
        files: files.map((s) => s.file),
        playlistId,
        source,
      }),
    }),
  // Same queue replace with bare file names (infinite-scroll play flow:
  // the full Song[] list is never materialized client-side).
  setQueueFiles: (currentFile: string | null, files: string[], source: QueueSource | null, playlistId: number | null = null) =>
    request<PlayerState>('/player/queue', {
      method: 'PUT',
      body: JSON.stringify({
        currentFile,
        files,
        playlistId,
        source,
      }),
    }),
  toggle: () => request<PlayerState>('/player/toggle', { method: 'POST' }),
  // Idempotent pause. positionSec/file let a delayed retry preserve the spot
  // the user actually paused at (the server estimate keeps running while the
  // request is lost); resume is the matching explicit, idempotent command.
  pause: (positionSec?: number | null, file?: string | null) =>
    request<PlayerState>('/player/pause', {
      method: 'POST',
      body: JSON.stringify({ file: file ?? null, positionSec: positionSec ?? null }),
    }),
  resume: () => request<PlayerState>('/player/resume', { method: 'POST' }),
  // expectedFile: conditional advance for the crossfade handoff — the
  // server skips the advance when the current track already moved on
  // (manual transport consumed it), converging instead of skipping a song.
  next: (expectedFile?: string | null, auto = false) =>
    request<PlayerState>(
      '/player/next',
      {
        method: 'POST',
        body: JSON.stringify({ expectedFile: expectedFile ?? null, auto }),
      },
    ),
  prev: () => request<PlayerState>('/player/prev', { method: 'POST' }),
  seek: (seconds: number) =>
    request<PlayerState>('/player/seek', { method: 'POST', body: JSON.stringify({ seconds }) }),
  seekBy: (deltaSeconds: number) =>
    request<PlayerState>('/player/seek-by', { method: 'POST', body: JSON.stringify({ deltaSeconds }) }),
  volume: (volume: number) =>
    request<PlayerState>('/player/volume', { method: 'POST', body: JSON.stringify({ volume }) }),
  shuffle: () => request<PlayerState>('/player/shuffle', { method: 'POST' }),
  repeat: () => request<PlayerState>('/player/repeat', { method: 'POST' }),

  // queue
  queueAdd: (file: string) =>
    request<PlayerState>('/player/queue/add', { method: 'POST', body: JSON.stringify({ file }) }),
  queueNext: (file: string) =>
    request<PlayerState>('/player/queue/play-next', { method: 'POST', body: JSON.stringify({ file }) }),
  queueRemove: (index: number) =>
    request<PlayerState>(`/player/queue/${index}`, { method: 'DELETE' }),
  queueReorder: (oldIndex: number, newIndex: number) =>
    request<PlayerState>('/player/queue/reorder', {
      method: 'PUT',
      body: JSON.stringify({ oldIndex, newIndex }),
    }),
  queueJump: (index: number) =>
    request<PlayerState>('/player/queue/jump', { method: 'POST', body: JSON.stringify({ index }) }),
  queueClear: () => request<PlayerState>('/player/queue/clear', { method: 'DELETE' }),
  queueRebuild: () =>
    request<{ rebuilt: boolean; state: PlayerState }>('/player/queue/rebuild', { method: 'POST' }),

  // AI DJ (Vohithe)
  aiDjState: () => request<AiDjState>('/ai-dj/state'),
  aiDjStart: (payload: AiDjStartPayload = {}) =>
    request<AiDjState>('/ai-dj/start', { method: 'POST', body: JSON.stringify(payload) }),
  aiDjStop: () => request<AiDjState>('/ai-dj/stop', { method: 'POST' }),
  aiDjFeedback: (payload: {
    sessionId?: string | null
    batchId?: string | null
    trackFile?: string | null
    event: string
  }) => request<void>('/ai-dj/feedback', { method: 'POST', body: JSON.stringify(payload) }),
  aiDjTranscribe: (audio: string, language?: string) =>
    request<{ text: string }>('/ai-dj/transcribe', {
      method: 'POST',
      body: JSON.stringify({ audio, language: language ?? navigator.language ?? 'pt-BR' }),
    }),
  aiDjSettings: () => request<AiDjSettings>('/ai-dj/settings'),
  aiDjUpdateSettings: (payload: AiDjStartPayload) =>
    request<AiDjSettings>('/ai-dj/settings', { method: 'PUT', body: JSON.stringify(payload) }),
  aiDjClipUrl: (id: string) =>
    `/api/v1/ai-dj/clips/${encodeURIComponent(id)}/audio?token=${encodeURIComponent(
      localStorage.getItem('hathor:token') ?? '',
    )}`,
  freshAiDjClipUrl: async (id: string) => {
    const token = await authToken()
    return `/api/v1/ai-dj/clips/${encodeURIComponent(id)}/audio?token=${encodeURIComponent(token)}`
  },

  // history
  downloadHistory: (page = 1, pageSize = 10) =>
    request<PagedResult<HistoryItem>>(`/history/downloads?page=${page}&pageSize=${pageSize}`),
  downloadHistoryFiles: () => request<string[]>('/history/downloads/files'),
  playedHistory: (page = 1, pageSize = 10) =>
    request<PagedResult<HistoryItem>>(`/history/played-songs?page=${page}&pageSize=${pageSize}`),
  playedHistoryFiles: () => request<string[]>('/history/played-songs/files'),
  playedPlaylistHistory: (page = 1, pageSize = 10) =>
    request<PagedResult<PlayedPlaylistItem>>(`/history/played-playlists?page=${page}&pageSize=${pageSize}`),
  recentlyPlayed: (limit = 15) => request<Song[]>(`/songs/recently-played?limit=${limit}`),
  recentlyDownloaded: (limit = 15) => request<Song[]>(`/songs/recently-downloaded?limit=${limit}`),

  // daily mix
  dailyMix: () => request<DailyMix>('/daily-mix'),
  regenerateMix: () => request<DailyMix>('/daily-mix/regenerate', { method: 'POST' }),

  // discover (out-of-library recommendations, packages/contracts/discover.md)
  discover: () => request<Discover>('/discover'),
  discoverInsights: (windowDays = 90) =>
    request<DiscoverInsights>(`/discover/insights?windowDays=${windowDays}`),
  tuneDiscover: (windowDays = 90) =>
    request<DiscoverTuning>(
      `/discover/tuning?windowDays=${windowDays}&k=10`,
      { method: 'POST' },
    ),
  refreshDiscover: () => request<Discover>('/discover/refresh', { method: 'POST' }),
  submitDiscoverFeedback: (feedback: DiscoverFeedback) =>
    request<void>('/discover/feedback', {
      method: 'POST',
      body: JSON.stringify(feedback),
    }),

  // ingest
  youtubeSearch: (q: string, limit = 5) =>
    request<VideoHit[]>(`/youtube/search?q=${encodeURIComponent(q)}&limit=${limit}`),
  // Embeddability probe + direct-audio fallback for blocked embeds.
  // resolveAudio forces the manifest lookup even for embeddable videos
  // (used when the iframe player itself reports an error afterwards).
  youtubePreview: (id: string, resolveAudio = false) =>
    request<{ id: string; embeddable: boolean; audioUrl: string | null }>(
      `/youtube/preview?id=${encodeURIComponent(id)}${resolveAudio ? '&resolveAudio=true' : ''}`,
    ),
  // Already-in-library pre-check (normalized title+artist, live files only).
  checkDownload: (title: string, artist: string | null) =>
    request<{ owned: boolean; file: string | null }>(
      `/downloads/check?title=${encodeURIComponent(title)}${artist ? `&artist=${encodeURIComponent(artist)}` : ''}`,
    ),
  submitDownload: (url: string, title: string, artist: string | null, isPodcast: boolean) =>
    request<{ qid: string }>('/downloads', {
      method: 'POST',
      body: JSON.stringify({ Url: url, Title: title, Artist: artist, IsPodcast: isPodcast }),
    }),
  downloadJobs: (limit = 8) => request<DownloadJob[]>(`/downloads?limit=${limit}`),
  retryDownload: (qid: string) => request<void>(`/downloads/${qid}/retry`, { method: 'POST' }),
  redownload: (url: string, title: string, artist: string | null, isPodcast = false) =>
    request<{ qid: string }>('/downloads/redownload', {
      method: 'POST',
      body: JSON.stringify({ Url: url, Title: title, Artist: artist, IsPodcast: isPodcast }),
    }),
  cancelDownload: (qid: string) => request<void>(`/downloads/${qid}`, { method: 'DELETE' }),
  batchDownload: (file: File, isPodcast: boolean) => {
    const form = new FormData()
    form.append('file', file)
    form.append('isPodcast', String(isPodcast))
    const token = localStorage.getItem('hathor:token')
    return fetch('/api/v1/downloads/batch', {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : {},
      body: form,
    }).then(async (res) => {
      if (!res.ok) throw new Error(await res.text())
      return (await res.json()) as { accepted: number; qids: string[] }
    })
  },

  // enrichment
  itunesTrending: (limit = 4) => request<string[]>(`/metadata/trending?limit=${limit}`),
  // iTunes candidates for auto-tagging (desktop search_itunes_multi).
  itunesOptions: (title: string, artist: string, limit = 5) =>
    request<ITunesHit[]>(
      `/metadata/itunes/options?title=${encodeURIComponent(title)}&artist=${encodeURIComponent(artist)}&limit=${limit}`,
    ),

  // lyrics
  songLyrics: (
    file: string,
    track: string,
    artist: string,
    durationSec?: number,
    refresh = false,
    chapterId?: number | null,
  ) =>
    request<Lyrics>(
      `/songs/${encodeURIComponent(file)}/lyrics?track=${encodeURIComponent(track)}&artist=${encodeURIComponent(artist)}${
        durationSec ? `&duration=${Math.round(durationSec)}` : ''
      }${refresh ? '&refresh=true' : ''}${chapterId != null ? `&chapterId=${chapterId}` : ''}`,
    ),
  searchLyrics: (track: string, artist: string) =>
    request<LyricsHit[]>('/lyrics/search', {
      method: 'POST',
      body: JSON.stringify({ Track: track, Artist: artist }),
    }),
  saveLyrics: (file: string, synced: string | null, plain: string | null, chapterId?: number | null) =>
    request<void>(`/songs/${encodeURIComponent(file)}/lyrics${chapterId != null ? `?chapterId=${chapterId}` : ''}`, {
      method: 'PUT',
      body: JSON.stringify({ Synced: synced, Plain: plain }),
    }),
  deleteLyrics: (file: string, chapterId?: number | null) =>
    request<void>(
      `/songs/${encodeURIComponent(file)}/lyrics${chapterId != null ? `?chapterId=${chapterId}` : ''}`,
      { method: 'DELETE' },
    ),
  lyricsOffset: (file: string, chapterId?: number | null) =>
    request<{ offsetMs: number }>(
      `/songs/${encodeURIComponent(file)}/lyrics/offset${chapterId != null ? `?chapterId=${chapterId}` : ''}`,
    ),
  setLyricsOffset: (file: string, offsetMs: number, chapterId?: number | null) =>
    request<{ offsetMs: number }>(`/songs/${encodeURIComponent(file)}/lyrics/offset${chapterId != null ? `?chapterId=${chapterId}` : ''}`, {
      method: 'PUT',
      body: JSON.stringify({ OffsetMs: offsetMs }),
    }),
  romanize: (text: string, isLrc: boolean) =>
    request<{ text: string }>('/lyrics/romanize', {
      method: 'POST',
      body: JSON.stringify({ Text: text, IsLrc: isLrc }),
    }),

  streamUrl: (file: string, isPodcast = false) =>
    `/api/v1/${isPodcast ? 'podcasts' : 'songs'}/${encodeURIComponent(file)}/stream?token=${encodeURIComponent(
      localStorage.getItem('hathor:token') ?? '',
    )}`,

  // Recovery URL for the audio engine: refreshes an expiring access token
  // first. The cached streamUrl above cannot do this — media requests never
  // pass through request()'s 401 path.
  freshStreamUrl: async (file: string, isPodcast = false) => {
    const token = await authToken()
    return `/api/v1/${isPodcast ? 'podcasts' : 'songs'}/${encodeURIComponent(file)}/stream?token=${encodeURIComponent(token)}`
  },

  // Embedded art as a cacheable URL (browser HTTP cache owns the bytes —
  // no base64 in JS state). <img> cannot set headers, so the token rides
  // along like streamUrl. `version` busts the cache after metadata edits.
  coverUrl: (file: string, version?: string | null, isPodcast = false) => {
    const q = new URLSearchParams()
    if (version) q.set('v', version)
    q.set('token', localStorage.getItem('hathor:token') ?? '')
    return `/api/v1/${isPodcast ? 'podcasts' : 'songs'}/${encodeURIComponent(file)}/cover?${q}`
  },

  // library metadata + delete
  song: (file: string, includeCover = false) =>
    request<Song>(`/songs/${encodeURIComponent(file)}?includeCover=${includeCover}`),
  patchSong: (file: string, patch: MetadataPatch) =>
    request<{ song: Song; resumeSec: number; pending: boolean }>(`/songs/${encodeURIComponent(file)}`, {
      method: 'PATCH',
      body: JSON.stringify({
        Title: patch.title ?? null,
        Artist: patch.artist ?? null,
        Album: patch.album ?? null,
        Year: patch.year ?? null,
        Genre: patch.genre ?? null,
        CoverArt: patch.coverArt ?? null,
      }),
    }),
  deleteSong: (file: string) =>
    request<void>(`/songs/${encodeURIComponent(file)}`, { method: 'DELETE' }),
  // Stashed edits for playing files (applied on track change).
  pendingEdits: () => request<PendingEdit[]>('/pending-edits'),
  discardPendingEdit: (file: string) =>
    request<void>(`/pending-edits/${encodeURIComponent(file)}`, { method: 'DELETE' }),
  // Resume across devices: snapshot this device's spot (pause/pagehide/
  // paused-seek, fire-and-forget) / read the latest spot from ANOTHER
  // device (excludeDevice = own id — never echo self). queueFiles carries
  // this device's Up Next (current first, capped) so the other side can
  // restore the full queue on tap.
  pushPlayback: (
    id: string,
    spot: {
      file: string
      positionSec: number
      isPodcast: boolean
      deviceLabel?: string | null
      queueFiles?: string[] | null
      queueSource?: QueueSource | null
    },
  ) =>
    request<boolean>('/sync/playback', {
      method: 'POST',
      body: JSON.stringify({
        deviceId: id,
        deviceLabel: spot.deviceLabel ?? null,
        file: spot.file,
        positionSec: spot.positionSec,
        isPodcast: spot.isPodcast,
        queueFiles: spot.queueFiles ?? null,
        queueSource: spot.queueSource
          ? { type: spot.queueSource.type, id: spot.queueSource.id ?? null }
          : null,
      }),
    }),
  latestPlayback: (excludeDevice?: string | null) =>
    request<PlaybackSpot | null>(
      `/sync/playback${excludeDevice ? `?excludeDevice=${encodeURIComponent(excludeDevice)}` : ''}`,
    ),

  // artists / albums (paged: omit page/pageSize for the legacy whole list)
  artists: () => request<string[]>('/artists'),
  artistSongs: (name: string, params: { search?: string; sort?: string; dir?: string; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.sort) q.set('sort', params.sort)
    if (params.dir) q.set('dir', params.dir)
    if (params.page !== undefined) q.set('page', String(params.page))
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize))
    const qs = q.toString()
    return request<Song[]>(`/artists/${encodeURIComponent(name)}/songs${qs ? `?${qs}` : ''}`)
  },
  artistSongCount: (name: string, search?: string) =>
    request<number>(
      `/artists/${encodeURIComponent(name)}/songs/count${search ? `?search=${encodeURIComponent(search)}` : ''}`,
    ),
  artistFiles: (name: string, search?: string, sort?: string, dir?: string) => {
    const q = new URLSearchParams()
    if (search) q.set('search', search)
    if (sort) q.set('sort', sort)
    if (dir) q.set('dir', dir)
    const qs = q.toString()
    return request<string[]>(
      `/artists/${encodeURIComponent(name)}/files${qs ? `?${qs}` : ''}`,
    )
  },
  artistImage: (name: string) =>
    request<Artwork>(`/artists/${encodeURIComponent(name)}/image`).catch(() => null),
  albums: () => request<string[]>('/albums'),
  albumSongs: (title: string, params: { search?: string; sort?: string; dir?: string; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.sort) q.set('sort', params.sort)
    if (params.dir) q.set('dir', params.dir)
    if (params.page !== undefined) q.set('page', String(params.page))
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize))
    const qs = q.toString()
    return request<Song[]>(`/albums/${encodeURIComponent(title)}/songs${qs ? `?${qs}` : ''}`)
  },
  albumSongCount: (title: string, search?: string) =>
    request<number>(
      `/albums/${encodeURIComponent(title)}/songs/count${search ? `?search=${encodeURIComponent(search)}` : ''}`,
    ),
  albumFiles: (title: string, search?: string, sort?: string, dir?: string) => {
    const q = new URLSearchParams()
    if (search) q.set('search', search)
    if (sort) q.set('sort', sort)
    if (dir) q.set('dir', dir)
    const qs = q.toString()
    return request<string[]>(
      `/albums/${encodeURIComponent(title)}/files${qs ? `?${qs}` : ''}`,
    )
  },
  albumImage: (title: string, artist?: string) =>
    request<Artwork>(
      `/albums/${encodeURIComponent(title)}/image${artist ? `?artist=${encodeURIComponent(artist)}` : ''}`,
    ).catch(() => null),

  // playlists
  playlists: () => request<Playlist[]>('/playlists'),
  playlistsWithCounts: () => request<PlaylistWithCount[]>('/playlists/with-counts'),
  createPlaylist: (title: string, description?: string | null, cover?: string | null) =>
    request<Playlist>('/playlists', {
      method: 'POST',
      body: JSON.stringify({ Title: title, Description: description ?? null, Cover: cover ?? null }),
    }),
  updatePlaylist: (id: number, patch: { title?: string; description?: string | null; thumbnail?: string | null }) =>
    request<Playlist>(`/playlists/${id}`, {
      method: 'PUT',
      body: JSON.stringify({
        Title: patch.title ?? null,
        Description: patch.description ?? null,
        Thumbnail: patch.thumbnail ?? null,
      }),
    }),
  deletePlaylist: (id: number) => request<void>(`/playlists/${id}`, { method: 'DELETE' }),
  playlistSongs: (id: number, params: { search?: string; sort?: string; dir?: string; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.sort) q.set('sort', params.sort)
    if (params.dir) q.set('dir', params.dir)
    if (params.page !== undefined) q.set('page', String(params.page))
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize))
    const qs = q.toString()
    return request<Song[]>(`/playlists/${id}/songs${qs ? `?${qs}` : ''}`)
  },
  playlistSongsCount: (id: number, search?: string) =>
    request<number>(`/playlists/${id}/songs/count${search ? `?search=${encodeURIComponent(search)}` : ''}`),
  playlistFiles: (id: number, search?: string, sort?: string, dir?: string) => {
    const q = new URLSearchParams()
    if (search) q.set('search', search)
    if (sort) q.set('sort', sort)
    if (dir) q.set('dir', dir)
    const qs = q.toString()
    return request<string[]>(`/playlists/${id}/files${qs ? `?${qs}` : ''}`)
  },
  addSongToPlaylist: (id: number, file: string) =>
    request<void>(`/playlists/${id}/songs`, { method: 'POST', body: JSON.stringify({ File: file }) }),
  removeSongFromPlaylist: (id: number, file: string) =>
    request<void>(`/playlists/${id}/songs/${encodeURIComponent(file)}`, { method: 'DELETE' }),
  songPlaylists: (file: string) =>
    request<number[]>(`/songs/${encodeURIComponent(file)}/playlists`),
  setSongPlaylists: (file: string, title: string, playlistIds: number[]) =>
    request<void>(`/songs/${encodeURIComponent(file)}/playlists`, {
      method: 'PUT',
      body: JSON.stringify({ Title: title, PlaylistIds: playlistIds }),
    }),

  // podcast tags
  podcastTags: () => request<PodcastTag[]>('/podcasttags'),
  podcastTagMap: () => request<Record<string, number[]>>('/podcasttags/map'),
  createPodcastTag: (name: string) =>
    request<{ id: number }>('/podcasttags', { method: 'POST', body: JSON.stringify({ Name: name }) }),
  renamePodcastTag: (id: number, name: string) =>
    request<void>(`/podcasttags/${id}`, { method: 'PUT', body: JSON.stringify({ Name: name }) }),
  deletePodcastTag: (id: number) => request<void>(`/podcasttags/${id}`, { method: 'DELETE' }),
  assignPodcastTag: (id: number, file: string) =>
    request<void>(`/podcasttags/${id}/episodes`, { method: 'POST', body: JSON.stringify({ File: file }) }),
  unassignPodcastTag: (id: number, file: string) =>
    request<void>(`/podcasttags/${id}/episodes/${encodeURIComponent(file)}`, { method: 'DELETE' }),

  // podcasts (paged: omit page/pageSize for the legacy whole list)
  podcasts: (params: { search?: string; tagId?: number | null; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.tagId !== undefined && params.tagId !== null) q.set('tagId', String(params.tagId))
    if (params.page !== undefined) q.set('page', String(params.page))
    if (params.pageSize !== undefined) q.set('pageSize', String(params.pageSize))
    const qs = q.toString()
    return request<Song[]>(`/podcasts${qs ? `?${qs}` : ''}`)
  },
  podcastCount: (search?: string, tagId?: number | null) => {
    const q = new URLSearchParams()
    if (search) q.set('search', search)
    if (tagId !== undefined && tagId !== null) q.set('tagId', String(tagId))
    const qs = q.toString()
    return request<number>(`/podcasts/count${qs ? `?${qs}` : ''}`)
  },
  podcastFiles: (search?: string, tagId?: number | null) => {
    const q = new URLSearchParams()
    if (search) q.set('search', search)
    if (tagId !== undefined && tagId !== null) q.set('tagId', String(tagId))
    const qs = q.toString()
    return request<string[]>(`/podcasts/files${qs ? `?${qs}` : ''}`)
  },
  podcastDetails: (file: string) => request<Song>(`/podcasts/${encodeURIComponent(file)}`),
  // Episode chapter marks ("timestamps"): media offsets in seconds.
  podcastTimestamps: (file: string) =>
    request<PodcastTimestamp[]>(`/podcasts/${encodeURIComponent(file)}/timestamps`),
  createPodcastTimestamp: (file: string, input: { name: string; startSecs: number; endSecs?: number | null }) =>
    request<PodcastTimestamp>(`/podcasts/${encodeURIComponent(file)}/timestamps`, {
      method: 'POST',
      body: JSON.stringify({ Name: input.name, StartSecs: input.startSecs, EndSecs: input.endSecs ?? null }),
    }),
  updatePodcastTimestamp: (
    file: string,
    id: number,
    input: { name: string; startSecs: number; endSecs?: number | null },
  ) =>
    request<PodcastTimestamp>(`/podcasts/${encodeURIComponent(file)}/timestamps/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ Name: input.name, StartSecs: input.startSecs, EndSecs: input.endSecs ?? null }),
    }),
  deletePodcastTimestamp: (file: string, id: number) =>
    request<void>(`/podcasts/${encodeURIComponent(file)}/timestamps/${id}`, { method: 'DELETE' }),
  patchPodcast: (file: string, patch: { title?: string | null; artist?: string | null; coverArt?: string | null }) =>
    request<{ song: Song; pending: boolean }>(`/podcasts/${encodeURIComponent(file)}`, {
      method: 'PATCH',
      body: JSON.stringify({
        Title: patch.title ?? null,
        Artist: patch.artist ?? null,
        CoverArt: patch.coverArt ?? null,
      }),
    }),
  deletePodcast: (file: string) =>
    request<void>(`/podcasts/${encodeURIComponent(file)}`, { method: 'DELETE' }),
  movePodcastToSongs: (file: string) =>
    request<Song>(`/podcasts/${encodeURIComponent(file)}/move-to-songs`, { method: 'POST' }),
  moveSongToPodcasts: (file: string) =>
    request<Song>(`/songs/${encodeURIComponent(file)}/move-to-podcasts`, { method: 'POST' }),
  scanPodcasts: () => request<{ added: number; updated: number }>('/podcasts/scan', { method: 'POST' }),
  scanSongs: () => request<{ added: number; updated: number }>('/songs/scan', { method: 'POST' }),
  // Loudness backfill (resumable: repeat until remaining hits 0).
  backfillLoudness: (limit = 20) =>
    request<{ scanned: number; remaining: number }>(
      `/songs/loudness/backfill?limit=${limit}`,
      { method: 'POST' },
    ),

  // settings + system
  settings: () => request<UserSettings>('/settings'),
  updateSettings: (patch: {
    volume?: number
    limitDownloads?: number
    crossfadeEnabled?: boolean
    crossfadeSeconds?: number
    lastRoute?: string | null
    browser?: string | null
    chapterSkip?: boolean
  }) =>
    request<UserSettings>('/settings', {
      method: 'PUT',
      body: JSON.stringify({
        Volume: patch.volume ?? null,
        LimitDownloads: patch.limitDownloads ?? null,
        CrossfadeEnabled: patch.crossfadeEnabled ?? null,
        CrossfadeSeconds: patch.crossfadeSeconds ?? null,
        LastRoute: patch.lastRoute ?? null,
        Browser: patch.browser ?? null,
        ChapterSkip: patch.chapterSkip ?? null,
      }),
    }),
  setPlayerSettings: (crossfadeEnabled: boolean, crossfadeSeconds: number) =>
    request<PlayerSettings>('/player/settings', {
      method: 'PUT',
      body: JSON.stringify({ CrossfadeEnabled: crossfadeEnabled, CrossfadeSeconds: crossfadeSeconds }),
    }),
  uploadBackground: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    const token = localStorage.getItem('hathor:token')
    return fetch('/api/v1/settings/background', {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : {},
      body: form,
    }).then(async (res) => {
      if (!res.ok) throw new Error(await res.text())
      return (await res.json()) as { filename: string }
    })
  },
  removeBackground: () => request<void>('/settings/background', { method: 'DELETE' }),
  backgroundUrl: () =>
    `/api/v1/settings/background/file?token=${encodeURIComponent(localStorage.getItem('hathor:token') ?? '')}`,
  ffmpegStatus: () => request<SystemStatus>('/system/ffmpeg'),
  libraryStatus: () => request<LibraryInfo[]>('/system/libraries'),
  runMaintenance: () =>
    request<{ ffmpeg: SystemStatus; libraries: LibraryInfo[]; allOk: boolean }>('/system/maintenance/run', {
      method: 'POST',
    }),
  // FFmpeg self-install (Settings → Download FFmpeg): poll the status
  // until state is ready/failed. Progress 0..1 (-1 while extracting).
  ffmpegDownloadStatus: () => request<FfmpegDownloadStatus>('/system/ffmpeg/download'),
  startFfmpegDownload: () =>
    request<FfmpegDownloadStatus>('/system/ffmpeg/download', { method: 'POST' }),
  // yt-dlp fallback-downloader status + self-install (Settings → yt-dlp).
  // Same status shape as FFmpeg (state/progress/exe/error); force=true
  // re-downloads the latest release even when one is installed (update).
  // The status Error field carries the --version output when found on PATH.
  ytdlpStatus: () => request<SystemStatus>('/system/ytdlp'),
  ytdlpDownloadStatus: () => request<FfmpegDownloadStatus>('/system/ytdlp/download'),
  startYtdlpDownload: (force = false) =>
    request<FfmpegDownloadStatus>(`/system/ytdlp/download${force ? '?force=true' : ''}`, {
      method: 'POST',
    }),
  // Live NuGet check for YoutubeExplode/TagLibSharp (Settings → YouTube
  // downloader). An update means "update the package and redeploy".
  checkLibraryUpdates: () => request<LibraryInfo[]>('/system/libraries/check', { method: 'POST' }),
  // YouTube bot-check protection (Settings → System): one-click PO-token
  // provider — no logins, no manual cookies. startProtection provisions
  // in the background (poll protectionStatus until ready/failed);
  // force=true re-provisions even when already enabled (update).
  protectionStatus: () => request<YoutubeProtectionStatus>('/system/youtube-protection'),
  startProtection: (force = false) =>
    request<YoutubeProtectionStatus>(
      `/system/youtube-protection${force ? '?force=true' : ''}`,
      { method: 'POST' },
    ),
  disableProtection: () =>
    request<YoutubeProtectionStatus>('/system/youtube-protection', { method: 'DELETE' }),
  logClient: (message: string, route?: string, stack?: string) =>
    request<void>('/logs/client', {
      method: 'POST',
      body: JSON.stringify({ Message: message, Route: route, Stack: stack }),
    }).catch(() => {}),

}
