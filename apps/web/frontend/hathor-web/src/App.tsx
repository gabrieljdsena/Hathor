import { Suspense, type JSX } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import Shell from './components/Shell'
import { LoadingState } from './components/ui/states'
import { lazyWithRetry } from './utils/lazyRetry'

// Route-level code splitting: each view loads on first navigation instead
// of bloating the initial bundle (player shell stays eager so audio + bar
// render instantly). Named-export views map to default for lazy().
// lazyWithRetry: a stale hashed chunk after a deploy reloads once instead
// of white-screening (see utils/lazyRetry).
const ApiKeys = lazyWithRetry(() => import('./views/ApiKeys'))
const Discover = lazyWithRetry(() => import('./views/Discover'))
const History = lazyWithRetry(() => import('./views/History'))
const Home = lazyWithRetry(() => import('./views/Home'))
const Download = lazyWithRetry(() => import('./views/Download'))
const Login = lazyWithRetry(() => import('./views/Login'))
const Mix = lazyWithRetry(() => import('./views/Mix'))
const PlaylistDetail = lazyWithRetry(() => import('./views/PlaylistDetail'))
const Playlists = lazyWithRetry(() => import('./views/Playlists'))
const Podcasts = lazyWithRetry(() => import('./views/Podcasts'))
const Settings = lazyWithRetry(() => import('./views/Settings'))
const Songs = lazyWithRetry(() => import('./views/Songs'))
const Artists = lazyWithRetry(() => import('./views/Library').then((m) => ({ default: m.Artists })))
const ArtistDetail = lazyWithRetry(() => import('./views/Library').then((m) => ({ default: m.ArtistDetail })))
const Albums = lazyWithRetry(() => import('./views/Library').then((m) => ({ default: m.Albums })))
const AlbumDetail = lazyWithRetry(() => import('./views/Library').then((m) => ({ default: m.AlbumDetail })))

function storedToken(): string | null {
  try {
    return localStorage.getItem('hathor:token')
  } catch {
    // Locked-down storage (private mode without access) must not crash boot.
    return null
  }
}

function Guard({ children }: { children: JSX.Element }) {
  const { username, ready } = useAuth()
  if (!ready) return <div className="min-h-screen bg-zinc-950" />
  if (!username && !storedToken()) return <Navigate to="/login" replace />
  return children
}

export default function App() {
  return (
    <Suspense fallback={<LoadingState label="Loading…" />}>
      <Routes>
        <Route path="/login" element={<Login mode="login" />} />
        <Route path="/register" element={<Login mode="register" />} />
        <Route
          path="/*"
          element={
            <Guard>
              <Shell />
            </Guard>
          }
        >
          <Route index element={<Home />} />
          <Route path="songs" element={<Songs />} />
          <Route path="download" element={<Download />} />
          <Route path="discover" element={<Discover />} />
          <Route path="podcasts" element={<Podcasts />} />
          <Route path="mix" element={<Mix />} />
          <Route path="history" element={<History />} />
          <Route path="api-keys" element={<ApiKeys />} />
          <Route path="settings" element={<Settings />} />
          <Route path="playlists" element={<Playlists />} />
          <Route path="playlists/:id" element={<PlaylistDetail />} />
          <Route path="artists" element={<Artists />} />
          <Route path="artists/:name" element={<ArtistDetail />} />
          <Route path="albums" element={<Albums />} />
          <Route path="albums/:title" element={<AlbumDetail />} />
          <Route path="*" element={<Home />} />
        </Route>
      </Routes>
    </Suspense>
  )
}
