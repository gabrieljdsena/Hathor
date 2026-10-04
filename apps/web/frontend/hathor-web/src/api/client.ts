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

export interface UserSettings {
  volume: number
  limitDownloads: number
  backgroundPath: string | null
  crossfadeEnabled: boolean
  crossfadeSeconds: number
  lastRoute: string | null
  browser: string | null
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

export interface SyncSnapshot {
  songs?: unknown[] | null
  podcasts?: unknown[] | null
  playlists?: unknown[] | null
  songLinks?: unknown[] | null
  podcastTags?: unknown[] | null
  podcastTagLinks?: unknown[] | null
  lyrics?: unknown[] | null
  musicHistory?: unknown[] | null
  playlistHistory?: unknown[] | null
  dailyMix?: unknown[] | null
  deletions?: unknown[] | null
}

export interface SyncSummary {
  songs: number
  podcasts: number
  playlists: number
  songLinks: number
  podcastTags: number
  podcastTagLinks: number
  lyrics: number
  musicHistory: number
  playlistHistory: number
  dailyMix: number
  deletions: number
}

export interface RemotePullResult {
  added: number
  downloadsStarted: number
  message: string
}

export interface RemotePushResult {
  rows: number
  message: string
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
}

function authHeaders(): HeadersInit {
  const token = localStorage.getItem('hathor:token')
  return token ? { Authorization: `Bearer ${token}` } : {}
}

// Single-flight silent refresh: on 401 (non-auth endpoints only),
// rotate via the stored refresh token once, then retry the request.
// Returns false when there is nothing to refresh with (logged out).
let refreshFlight: Promise<boolean> | null = null

function tryRefresh(): Promise<boolean> {
  const refreshToken = localStorage.getItem('hathor:refresh')
  if (!refreshToken) return Promise.resolve(false)
  refreshFlight ??= fetch(`${BASE}/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken }),
  })
    .then(async (res) => {
      if (!res.ok) throw new Error('refresh rejected')
      const tokens = (await res.json()) as { accessToken: string; refreshToken: string }
      localStorage.setItem('hathor:token', tokens.accessToken)
      localStorage.setItem('hathor:refresh', tokens.refreshToken)
      return true
    })
    .catch(() => {
      localStorage.removeItem('hathor:token')
      localStorage.removeItem('hathor:refresh')
      return false
    })
    .finally(() => {
      refreshFlight = null
    })
  return refreshFlight
}

function logoutLocal() {
  localStorage.removeItem('hathor:token')
  localStorage.removeItem('hathor:refresh')
  if (!location.pathname.startsWith('/login')) location.href = '/login'
}

async function request<T>(path: string, init?: RequestInit, retry = true): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders(), ...init?.headers },
  })
  if (res.status === 401 && retry && !path.startsWith('/auth/')) {
    if (await tryRefresh()) return request<T>(path, init, false)
    logoutLocal()
    throw new Error('Unauthorized')
  }
  if (res.status === 401) {
    logoutLocal()
    throw new Error('Unauthorized')
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

  // library
  songs: (params: { search?: string; sort?: string; dir?: string; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (params.search) q.set('search', params.search)
    if (params.sort) q.set('sort', params.sort)
    if (params.dir) q.set('dir', params.dir)
    q.set('page', String(params.page ?? 1))
    q.set('pageSize', String(params.pageSize ?? 100))
    return request<Song[]>(`/songs?${q}`)
  },
  songCount: (search?: string) =>
    request<number>(`/songs/count${search ? `?search=${encodeURIComponent(search)}` : ''}`),

  // player state + headless controls (plan §4 — same surface external clients use)
  playerState: () => request<PlayerState>('/player/state'),
  nowPlaying: () =>
    request<{ title: string | null; artist: string | null; coverUrl: string | null; positionSec: number; durationSec: number | null }>(
      '/player/now-playing',
    ),
  play: (file?: string, isPodcast?: boolean) =>
    request<PlayerState>('/player/play', {
      method: 'POST',
      body: JSON.stringify({ file: file ?? null, isPodcast: isPodcast ?? null }),
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
  toggle: () => request<PlayerState>('/player/toggle', { method: 'POST' }),
  pause: () => request<PlayerState>('/player/pause', { method: 'POST' }),
  next: () => request<PlayerState>('/player/next', { method: 'POST', body: '{}' }),
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

  // history
  downloadHistory: (page = 1, pageSize = 10) =>
    request<PagedResult<HistoryItem>>(`/history/downloads?page=${page}&pageSize=${pageSize}`),
  playedHistory: (page = 1, pageSize = 10) =>
    request<PagedResult<HistoryItem>>(`/history/played-songs?page=${page}&pageSize=${pageSize}`),
  playedPlaylistHistory: (page = 1, pageSize = 10) =>
    request<PagedResult<PlayedPlaylistItem>>(`/history/played-playlists?page=${page}&pageSize=${pageSize}`),
  recentlyPlayed: (limit = 15) => request<Song[]>(`/songs/recently-played?limit=${limit}`),
  recentlyDownloaded: (limit = 15) => request<Song[]>(`/songs/recently-downloaded?limit=${limit}`),

  // daily mix
  dailyMix: () => request<DailyMix>('/daily-mix'),
  regenerateMix: () => request<DailyMix>('/daily-mix/regenerate', { method: 'POST' }),

  // ingest
  youtubeSearch: (q: string, limit = 5) =>
    request<VideoHit[]>(`/youtube/search?q=${encodeURIComponent(q)}&limit=${limit}`),
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
  songLyrics: (file: string, track: string, artist: string, durationSec?: number, refresh = false) =>
    request<Lyrics>(
      `/songs/${encodeURIComponent(file)}/lyrics?track=${encodeURIComponent(track)}&artist=${encodeURIComponent(artist)}${
        durationSec ? `&duration=${Math.round(durationSec)}` : ''
      }${refresh ? '&refresh=true' : ''}`,
    ),
  searchLyrics: (track: string, artist: string) =>
    request<LyricsHit[]>('/lyrics/search', {
      method: 'POST',
      body: JSON.stringify({ Track: track, Artist: artist }),
    }),
  saveLyrics: (file: string, synced: string | null, plain: string | null) =>
    request<void>(`/songs/${encodeURIComponent(file)}/lyrics`, {
      method: 'PUT',
      body: JSON.stringify({ Synced: synced, Plain: plain }),
    }),
  deleteLyrics: (file: string) =>
    request<void>(`/songs/${encodeURIComponent(file)}/lyrics`, { method: 'DELETE' }),
  lyricsOffset: (file: string) =>
    request<{ offsetMs: number }>(`/songs/${encodeURIComponent(file)}/lyrics/offset`),
  setLyricsOffset: (file: string, offsetMs: number) =>
    request<{ offsetMs: number }>(`/songs/${encodeURIComponent(file)}/lyrics/offset`, {
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

  // library metadata + delete
  song: (file: string, includeCover = false) =>
    request<Song>(`/songs/${encodeURIComponent(file)}?includeCover=${includeCover}`),
  patchSong: (file: string, patch: MetadataPatch) =>
    request<{ song: Song; resumeSec: number }>(`/songs/${encodeURIComponent(file)}`, {
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

  // artists / albums
  artists: () => request<string[]>('/artists'),
  artistSongs: (name: string) => request<Song[]>(`/artists/${encodeURIComponent(name)}/songs`),
  artistImage: (name: string) =>
    request<Artwork>(`/artists/${encodeURIComponent(name)}/image`).catch(() => null),
  albums: () => request<string[]>('/albums'),
  albumSongs: (title: string) => request<Song[]>(`/albums/${encodeURIComponent(title)}/songs`),
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
  playlistSongs: (id: number) => request<Song[]>(`/playlists/${id}/songs`),
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

  // podcasts
  podcasts: () => request<Song[]>('/podcasts'),
  podcastDetails: (file: string) => request<Song>(`/podcasts/${encodeURIComponent(file)}`),
  patchPodcast: (file: string, patch: { title?: string | null; artist?: string | null; coverArt?: string | null }) =>
    request<Song>(`/podcasts/${encodeURIComponent(file)}`, {
      method: 'PATCH',
      body: JSON.stringify({
        Title: patch.title ?? null,
        Artist: patch.artist ?? null,
        CoverArt: patch.coverArt ?? null,
      }),
    }),
  deletePodcast: (file: string) =>
    request<void>(`/podcasts/${encodeURIComponent(file)}`, { method: 'DELETE' }),
  scanPodcasts: () => request<{ added: number; updated: number }>('/podcasts/scan', { method: 'POST' }),
  scanSongs: () => request<{ added: number; updated: number }>('/songs/scan', { method: 'POST' }),

  // settings + system
  settings: () => request<UserSettings>('/settings'),
  updateSettings: (patch: {
    volume?: number
    limitDownloads?: number
    crossfadeEnabled?: boolean
    crossfadeSeconds?: number
    lastRoute?: string | null
    browser?: string | null
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
      }),
    }),
  playerSettings: () => request<PlayerSettings>('/player/settings'),
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
  // Live NuGet check for YoutubeExplode/TagLibSharp (Settings → YouTube
  // downloader). An update means "update the package and redeploy".
  checkLibraryUpdates: () => request<LibraryInfo[]>('/system/libraries/check', { method: 'POST' }),
  logClient: (message: string, route?: string, stack?: string) =>
    request<void>('/logs/client', {
      method: 'POST',
      body: JSON.stringify({ Message: message, Route: route, Stack: stack }),
    }).catch(() => {}),

  // sync snapshot exchange (desktop/mobile compat)
  syncExport: (sinceId = 0) => request<SyncSnapshot>(`/sync/export?sinceId=${sinceId}`),
  syncImport: (snapshot: SyncSnapshot) =>
    request<SyncSummary>('/sync/import', { method: 'POST', body: JSON.stringify(snapshot) }),
  // Per-library pull from the desktop TiDB remote (desktop
  // sync_remote_to_local_and_download, split in two): merges remote rows
  // and queues downloads for files missing on disk.
  pullSongs: () => request<RemotePullResult>('/sync/pull-songs', { method: 'POST' }),
  pullPodcasts: () => request<RemotePullResult>('/sync/pull-podcasts', { method: 'POST' }),
  // Push this user's rows back into the desktop TiDB remote (desktop push).
  pushSongs: () => request<RemotePushResult>('/sync/push-songs', { method: 'POST' }),
  pushPodcasts: () => request<RemotePushResult>('/sync/push-podcasts', { method: 'POST' }),
}
