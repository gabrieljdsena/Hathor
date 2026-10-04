import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { api } from '../api/client'
import Brand from '../components/ui/Brand'
import { PrimaryButton, TextField } from '../components/ui/fields'

export default function Login({ mode }: { mode: 'login' | 'register' }) {
  const { login, register } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [registrationOpen, setRegistrationOpen] = useState<boolean | null>(null)

  // Single-account mode: the server closes registration after the first user.
  useEffect(() => {
    if (mode !== 'register') return
    api
      .authStatus()
      .then((s) => setRegistrationOpen(s.registrationOpen))
      .catch(() => setRegistrationOpen(true))
  }, [mode])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (mode === 'register' && password !== confirmPassword) {
      setError('Passwords do not match.')
      return
    }
    setBusy(true)
    try {
      if (mode === 'login') await login(username, password, rememberMe)
      else await register(username, password, rememberMe)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-zinc-950 px-4 relative overflow-hidden">
      <div className="absolute -top-[15%] -left-[5%] w-[45vw] h-[45vw] bg-orange-500/10 rounded-full blur-[120px] pointer-events-none" />
      <div className="absolute -bottom-[15%] right-[15%] w-[35vw] h-[35vw] bg-orange-600/10 rounded-full blur-[120px] pointer-events-none" />
      <div className="w-full max-w-md rounded-2xl border border-white/10 bg-black/40 backdrop-blur-2xl p-8 relative">
        <div className="flex items-center gap-3 mb-6">
          <Brand boxClass="w-10 h-10 rounded-xl" wordmark={false} />
          <h1 className="text-2xl font-bold text-zinc-100">Hathor</h1>
        </div>
          {mode === 'register' && registrationOpen === false ? (
            <p className="text-sm text-zinc-400">
              Registration is closed — this server allows a single account.{' '}
              <Link to="/login" className="text-orange-400 hover:text-orange-300">
                Sign in
              </Link>
            </p>
          ) : (
            <form onSubmit={submit} className="flex flex-col gap-4">
          <TextField
            label="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            placeholder="Username"
            autoComplete="username"
          />
          <TextField
            label="Password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="Password"
            autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
          />
          {mode === 'register' && (
            <TextField
              label="Confirm password"
              type="password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              placeholder="Repeat password"
              autoComplete="new-password"
            />
          )}
          {error && <p className="text-sm text-red-400">{error}</p>}
          <label className="flex items-center gap-2 text-sm text-zinc-400 select-none cursor-pointer">
            <input
              type="checkbox"
              checked={rememberMe}
              onChange={(e) => setRememberMe(e.target.checked)}
              className="w-4 h-4 rounded accent-orange-500 cursor-pointer"
            />
            Remember me for 30 days
          </label>
          <PrimaryButton type="submit" loading={busy} className="py-3 rounded-2xl text-base">
            {mode === 'login' ? 'Sign in' : 'Create account'}
          </PrimaryButton>
        </form>
          )}
        <p className="text-sm text-zinc-500 mt-4">
          {mode === 'login' ? (
            <>
              No account? <Link to="/register" className="text-orange-400 hover:text-orange-300">Register</Link>
            </>
          ) : (
            <>
              Have an account? <Link to="/login" className="text-orange-400 hover:text-orange-300">Sign in</Link>
            </>
          )}
        </p>
      </div>
    </div>
  )
}
