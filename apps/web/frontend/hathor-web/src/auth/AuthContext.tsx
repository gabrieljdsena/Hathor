import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'

interface AuthState {
  username: string | null
  ready: boolean
  login: (username: string, password: string, rememberMe?: boolean) => Promise<void>
  register: (username: string, password: string, rememberMe?: boolean) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [username, setUsername] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const navigate = useNavigate()

  useEffect(() => {
    const token = localStorage.getItem('hathor:token')
    if (!token) {
      setReady(true)
      return
    }
    api
      .me()
      .then((me) => setUsername(me.username))
      .catch(() => {
        // request() already tried silent refresh; still failing means the
        // session is gone — it cleared storage and redirected.
      })
      .finally(() => setReady(true))
  }, [])

  const login = useCallback(
    async (u: string, p: string, rememberMe = false) => {
      const res = await api.login(u, p, rememberMe)
      localStorage.setItem('hathor:token', res.accessToken)
      localStorage.setItem('hathor:refresh', res.refreshToken)
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
      localStorage.removeItem('hathor:token')
      localStorage.removeItem('hathor:refresh')
      setUsername(null)
      navigate('/login')
    }
  }, [navigate])

  return (
    <AuthContext.Provider value={{ username, ready, login, register, logout }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
