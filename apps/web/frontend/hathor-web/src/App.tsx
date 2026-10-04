import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import Shell from './components/Shell'
import ApiKeys from './views/ApiKeys'
import Discover from './views/Discover'
import History from './views/History'
import Home from './views/Home'
import Download from './views/Download'
import { AlbumDetail, Albums, ArtistDetail, Artists } from './views/Library'
import Login from './views/Login'
import Mix from './views/Mix'
import PlaylistDetail from './views/PlaylistDetail'
import Playlists from './views/Playlists'
import Podcasts from './views/Podcasts'
import Settings from './views/Settings'
import Songs from './views/Songs'
import type { JSX } from 'react'

function Guard({ children }: { children: JSX.Element }) {
  const { username, ready } = useAuth()
  if (!ready) return <div className="min-h-screen bg-zinc-950" />
  if (!username && !localStorage.getItem('hathor:token')) return <Navigate to="/login" replace />
  return children
}

export default function App() {
  return (
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
  )
}
