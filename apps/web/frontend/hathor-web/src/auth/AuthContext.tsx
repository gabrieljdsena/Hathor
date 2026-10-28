import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'

const USERNAME_KEY = 'hathor:username'

interface AuthState {
  username: string | null
  ready: boolean
  login: (username: string, password: string, rememberMe?: boolean) => Promise<void>
  register: (username: string, password: string, rememberMe?: boolean) => Promise<void>
  logout: () => Promise<void>
  signOutLocal: () => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [username, setUsername] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const navigate = useNavigate()

  const signOutLocal = useCallback(() => {
    localStorage.removeItem('hathor:token')
    localStorage.removeItem('hathor:refresh')
    localStorage.removeItem(USERNAME_KEY)
    setUsername(null)
    navigate('/login')
  }, [navigate])

  useEffect(() => {
    const token = localStorage.getItem('hathor:token')
    if (!token) {
      localStorage.removeItem(USERNAME_KEY)
      setReady(true)
      return
    }
    // Show the last known user immediately; me() only confirms the session.
    // A transient failure (server restart, offline) keeps the cached user
    // so the shell stays usable instead of bouncing to the login screen.
    const cached = localStorage.getItem(USERNAME_KEY)
    if (cached) setUsername(cached)
    api
      .me()
      .then((me) => {
        localStorage.setItem(USERNAME_KEY, me.username)
        setUsername(me.username)
      })
      .catch(() => {
        // request() already tried silent refresh. If the session was
        // explicitly rejected it cleared storage; keep the cached user only
        // while a token still exists.
        if (!localStorage.getItem('hathor:token')) {
          localStorage.removeItem(USERNAME_KEY)
          setUsername(null)
        }
      })
      .finally(() => setReady(true))
  }, [])

  // Sign out every tab when one of them clears the shared refresh token.
  useEffect(() => {
    const onStorage = (e: StorageEvent) => {
      if (e.key !== null && e.key !== 'hathor:refresh') return
      if (localStorage.getItem('hathor:refresh')) return
      setUsername(null)
      navigate('/login')
    }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
  }, [navigate])

  const login = useCallback(
    async (u: string, p: string, rememberMe = false) => {
      const res = await api.login(u, p, rememberMe)
      localStorage.setItem('hathor:token', res.accessToken)
      localStorage.setItem('hathor:refresh', res.refreshToken)
      localStorage.setItem(USERNAME_KEY, res.username)
      setUsername(res.username)
      navigate('/')
    },
    [navigate],
  )

  const register = useCallback(
    async (u: string, p: string, rememberMe = false) => {
      const res = await api.register(u, p, rememberMe)
      localStorage.setItem('hathor:token', res.accessToken)
      localStorage.setItem('hathor:refresh', res.refreshToken)
      localStorage.setItem(USERNAME_KEY, res.username)
      setUsername(res.username)
      navigate('/')
    },
    [navigate],
  )

  // Server logout first (revokes the session so the refresh token dies),
  // then wipe local state no matter what the server said.
  const logout = useCallback(async () => {
    try {
      await api.logout()
    } finally {
      signOutLocal()
    }
  }, [signOutLocal])

  return (
    <AuthContext.Provider value={{ username, ready, login, register, logout, signOutLocal }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
