import { Component, type ReactNode } from 'react'
import { api } from '../../api/client'

// Catches render crashes anywhere below it (a view throwing on bad data,
// a failed lazy chunk after a deploy) and shows a recovery screen instead
// of the permanent white screen an uncaught error produces (React unmounts
// the whole root). The player bar lives above the Shell-level boundary, so
// audio keeps playing through a view crash. Remounts (via resetKey, wired
// to the pathname) clear the error — navigating away always recovers.
export default class RouteErrorBoundary extends Component<
  { resetKey: string; children: ReactNode },
  { error: Error | null }
> {
  state: { error: Error | null } = { error: null }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  componentDidCatch(error: Error) {
    // Same bridge as main.tsx's window handlers (best-effort, never throws).
    try {
      if (typeof localStorage !== 'undefined' && localStorage.getItem('hathor:token')) {
        void api
          .logClient(`boundary: ${error.message}`.slice(0, 2000), location.pathname)
          .catch(() => {})
      }
    } catch {
      /* logging must never break the app */
    }
  }

  componentDidUpdate(prevProps: { resetKey: string }) {
    if (prevProps.resetKey !== this.props.resetKey && this.state.error !== null) {
      this.setState({ error: null })
    }
  }

  render() {
    if (this.state.error === null) return this.props.children
    return (
      <div className="flex flex-col items-center justify-center gap-4 py-24 px-6 text-center">
        <div className="text-5xl" aria-hidden="true">
          🎵💥
        </div>
        <h1 className="text-xl font-bold text-zinc-100">This page crashed</h1>
        <p className="text-sm text-zinc-500 max-w-md">
          Nothing was lost — your queue keeps playing. Reloading usually fixes it (for example
          after an update landed while this tab was open).
        </p>
        <div className="flex gap-3">
          <button
            onClick={() => window.location.reload()}
            className="px-5 py-2.5 text-sm font-medium text-white bg-orange-500 hover:bg-orange-400 rounded-lg transition-all shadow-lg shadow-orange-500/20 cursor-pointer"
          >
            Reload page
          </button>
          <a
            href="/"
            className="px-5 py-2.5 text-sm font-medium text-zinc-200 bg-white/5 hover:bg-white/10 border border-white/10 rounded-lg transition-all"
          >
            Go home
          </a>
        </div>
      </div>
    )
  }
}
