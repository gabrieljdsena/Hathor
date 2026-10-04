import { Suspense, lazy, type JSX } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import Shell from './components/Shell'
import { LoadingState } from './components/ui/states'

// Route-level code splitting: each view loads on first navigation instead
// of bloating the initial bundle (player shell stays eager so audio + bar
// render instantly). Named-export views map to default for lazy().
const ApiKeys = lazy(() => import('./views/ApiKeys'))
const Discover = lazy(() => import('./views/Discover'))
const History = lazy(() => import('./views/History'))
const Home = lazy(() => import('./views/Home'))
const Download = lazy(() => import('./views/Download'))
const Login = lazy(() => import('./views/Login'))
const Mix = lazy(() => import('./views/Mix'))
const PlaylistDetail = lazy(() => import('./views/PlaylistDetail'))
const Playlists = lazy(() => import('./views/Playlists'))
const Podcasts = lazy(() => import('./views/Podcasts'))
const Settings = lazy(() => import('./views/Settings'))
const Songs = lazy(() => import('./views/Songs'))
const Artists = lazy(() => import('./views/Library').then((m) => ({ default: m.Artists })))
const ArtistDetail = lazy(() => import('./views/Library').then((m) => ({ default: m.ArtistDetail })))
const Albums = lazy(() => import('./views/Library').then((m) => ({ default: m.Albums })))
const AlbumDetail = lazy(() => import('./views/Library').then((m) => ({ default: m.AlbumDetail })))

function Guard({ children }: { children: JSX.Element }) {
  const { username, ready } = useAuth()
  if (!ready) return <div className="min-h-screen bg-zinc-950" />
  if (!username && !localStorage.getItem('hathor:token')) return <Navigate to="/login" replace />
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
