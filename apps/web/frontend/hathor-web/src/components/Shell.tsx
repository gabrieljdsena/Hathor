import { useEffect, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { usePlayer } from '../store/player'
import PlayerBar from './PlayerBar'
import LyricsSheet from './LyricsSheet'
import NowPlaying from './NowPlaying'
import QueueSheet from './QueueSheet'
import Brand from './ui/Brand'
import Icon, { type IconName } from './ui/icons'

const linkClass = ({ isActive }: { isActive: boolean }) =>
  `nav-link group flex items-center p-3 rounded-xl transition-all duration-300 relative overflow-hidden ${
    isActive
      ? 'active-nav text-white bg-orange-500/10'
      : 'text-zinc-400 hover:text-white hover:bg-orange-500/10'
  }`

function NavItem({
  to,
  end,
  icon,
  label,
  collapsed,
}: {
  to: string
  end?: boolean
  icon: IconName
  label: string
  collapsed: boolean
}) {
  return (
    <NavLink to={to} end={end} title={label} className={(p) => `${linkClass(p)}${collapsed ? ' justify-center' : ''}`}>
      <div className="nav-indicator absolute left-0 top-1/4 bottom-1/4 w-1 bg-orange-500 rounded-r-full scale-y-0 transition-transform duration-300" />
      <Icon
        name={icon}
        className={`flex-shrink-0 transition-all duration-300 group-hover:scale-110 w-6 h-6 ${
          collapsed ? '' : 'mr-3'
        }`}
      />
      {!collapsed && <span className="font-medium tracking-wide truncate">{label}</span>}
    </NavLink>
  )
}

// Sidebar + bottom player bar shell. Classes cloned from Music Player/ui/index.html
// (desktop titlebar/resize zones intentionally dropped — responsive web only).
export default function Shell() {
  const { logout } = useAuth()
  const [queueOpen, setQueueOpen] = useState(false)
  const [lyricsOpen, setLyricsOpen] = useState(false)
  const [nowPlayingOpen, setNowPlayingOpen] = useState(false)
  const [signingOut, setSigningOut] = useState(false)
  // Collapsible icon rail (desktop #sidebar-toggle → w-64/w-20). Narrow
  // screens start collapsed and hide the toggle, like the original.
  const [collapsed, setCollapsed] = useState(
    () => typeof window !== 'undefined' && window.innerWidth <= 700,
  )
  const [narrow, setNarrow] = useState(
    () => typeof window !== 'undefined' && window.innerWidth <= 700,
  )
  const { data: settings } = useQuery({ queryKey: ['settings'], queryFn: api.settings })

  useEffect(() => {
    const onResize = () => {
      const isNarrow = window.innerWidth <= 700
      setNarrow(isNarrow)
      if (isNarrow) setCollapsed(true)
    }
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])

  // Keyboard shortcuts (desktop Now Playing & shortcuts): Space play/pause,
  // ←/→ seek ±10s, ↑/↓ volume. Ignored while typing in inputs.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement | null
      const typing =
        el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el?.isContentEditable === true
      if (typing) return
      const store = usePlayer.getState()
      if (e.code === 'Space') {
        e.preventDefault()
        void store.toggle()
      } else if (e.key === 'ArrowRight') {
        void api.seekBy(10).then((s) => {
          usePlayer.setState(s)
          store.syncAudio()
        })
      } else if (e.key === 'ArrowLeft') {
        void api.seekBy(-10).then((s) => {
          usePlayer.setState(s)
          store.syncAudio()
        })
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        const v = Math.min(1, store.volume + 0.05)
        void api.volume(v).then((s) => usePlayer.setState({ volume: s.volume }))
      } else if (e.key === 'ArrowDown') {
        e.preventDefault()
        const v = Math.max(0, store.volume - 0.05)
        void api.volume(v).then((s) => usePlayer.setState({ volume: s.volume }))
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <div className="bg-zinc-950 text-zinc-300 font-sans overflow-hidden selection:bg-orange-500/30 h-screen w-screen relative z-0 flex flex-col">
      {/* Custom background image under the glass (desktop apply_background:
          dark gradient overlay + cover + centered + fixed). */}
      {settings?.backgroundPath && (
        <div
          aria-hidden="true"
          className="absolute inset-0 z-[-1] pointer-events-none"
          style={{
            backgroundImage: `linear-gradient(rgba(9, 9, 11, 0.75), rgba(9, 9, 11, 0.75)), url('${api.backgroundUrl()}')`,
            backgroundSize: 'cover',
            backgroundPosition: 'center',
            backgroundRepeat: 'no-repeat',
            backgroundAttachment: 'fixed',
          }}
        />
      )}
      <div className="absolute -top-[15%] -left-[5%] w-[45vw] h-[45vw] bg-orange-500/10 rounded-full blur-[120px] pointer-events-none z-[-1]" />
      <div className="absolute -bottom-[15%] right-[15%] w-[35vw] h-[35vw] bg-orange-600/10 rounded-full blur-[120px] pointer-events-none z-[-1]" />

      <div id="page" className="flex flex-row w-full flex-1 mb-36 min-[1100px]:mb-28" style={{ minHeight: 0 }}>
        <aside
          id="sidebar"
          className={`flex-shrink-0 flex flex-col border-r border-orange-500/10 bg-black/40 backdrop-blur-2xl z-10 relative shadow-[4px_0_24px_rgba(0,0,0,0.5)] overflow-hidden transition-all duration-300 ease-[cubic-bezier(0.4,0,0.2,1)] ${
            collapsed ? 'w-20' : 'w-64'
          }`}
        >
          <div className="absolute inset-0 bg-gradient-to-b from-white/[0.04] to-transparent pointer-events-none" />
          <div
            id="sidebar-header"
            className={`p-5 flex items-center gap-3 border-b border-white/5 h-20 flex-shrink-0 ${
              collapsed ? 'justify-center' : 'justify-between'
            }`}
          >
            <div className="flex items-center gap-3 overflow-hidden">
              <Brand wordmark={!collapsed} />
            </div>
            {!narrow && (
              <button
                onClick={() => setCollapsed((v) => !v)}
                title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
                className="text-zinc-500 hover:text-orange-400 p-1.5 rounded-xl hover:bg-white/10 transition-all duration-300 flex-shrink-0 outline-none cursor-pointer"
              >
                <Icon
                  name="chevronsLeft"
                  className={`w-5 h-5 transition-transform duration-300 ease-in-out ${
                    collapsed ? 'rotate-180' : ''
                  }`}
                />
              </button>
            )}
          </div>

          <nav className={`flex flex-col flex-1 p-4 gap-2 overflow-y-auto ${collapsed ? 'items-center' : ''}`}>
            <NavItem to="/" end icon="home" label="Home" collapsed={collapsed} />
            <NavItem to="/songs" icon="musicNote" label="Songs" collapsed={collapsed} />
            <NavItem to="/download" icon="download" label="Download" collapsed={collapsed} />
            <NavItem to="/podcasts" icon="mic" label="Podcasts" collapsed={collapsed} />
            <NavItem to="/playlists" icon="list" label="Playlists" collapsed={collapsed} />
            <NavItem to="/history" icon="clock" label="History" collapsed={collapsed} />
            <NavItem to="/artists" icon="users" label="Artists" collapsed={collapsed} />
            <NavItem to="/albums" icon="tag" label="Albums" collapsed={collapsed} />
            <NavItem to="/api-keys" icon="key" label="API Keys" collapsed={collapsed} />
            <NavItem to="/settings" icon="gear" label="Settings" collapsed={collapsed} />
            {/* Server logout (revokes the session) with local fallback. */}
            <button
              onClick={() => {
                if (signingOut) return
                setSigningOut(true)
                void logout().finally(() => setSigningOut(false))
              }}
              title="Sign out"
              className={`mt-auto flex items-center gap-3 p-3 rounded-xl text-zinc-400 hover:text-white hover:bg-orange-500/10 transition-all duration-300 cursor-pointer disabled:opacity-60 ${
                collapsed ? 'justify-center' : ''
              }`}
              disabled={signingOut}
            >
              {signingOut ? (
                <span className="spinner-btn" aria-hidden="true" />
              ) : (
                <Icon name="logout" className="w-6 h-6 flex-shrink-0" />
              )}
              {!collapsed && (
                <span className="font-medium tracking-wide text-sm">
                  {signingOut ? 'Signing out…' : 'Sign out'}
                </span>
              )}
            </button>
          </nav>
        </aside>

        <div className="flex flex-col flex-grow min-w-0 p-4 sm:p-8 lg:p-12 bg-transparent relative">
          <div className="overflow-y-auto overflow-x-hidden flex-grow" id="main">
            <Outlet />
          </div>
          {/* Inside the content column (like the original #lyrics-container-view):
              inset-x-0 spans content only so the sidebar stays visible, and
              bottom-10 clears the player bar via the page margin. Shifts
              left of the queue panel (sm+) while it is open. */}
          <LyricsSheet open={lyricsOpen} onClose={() => setLyricsOpen(false)} queueOpen={queueOpen} />
        </div>
      </div>

      <PlayerBar
        onToggleQueue={() => setQueueOpen((v) => !v)}
        onToggleLyrics={() => setLyricsOpen((v) => !v)}
        onOpenNowPlaying={() => setNowPlayingOpen(true)}
        queueActive={queueOpen}
        lyricsActive={lyricsOpen}
      />
      <QueueSheet open={queueOpen} onClose={() => setQueueOpen(false)} />
      {nowPlayingOpen && <NowPlaying onClose={() => setNowPlayingOpen(false)} />}
    </div>
  )
}
