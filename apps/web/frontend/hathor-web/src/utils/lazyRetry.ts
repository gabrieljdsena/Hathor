import { lazy, type ComponentType } from 'react'

// Route-level lazy() wrapper: a failed chunk fetch (deploy replaced the
// hashed assets while this tab was open, or a network blip) rejects the
// import promise, and without an error boundary that unmounts the app into
// a permanent white screen. Retry once with a hard reload so the tab picks
// up the fresh index.html + chunks; the session flag stops reload loops
// (a second consecutive failure renders the error boundary instead).
//
// `ComponentType<any>`: every route component (whatever its props) is
// assignable, and `lazy()` keeps working — including `Promise.reject`
// importers in tests (`never` flows into anything).
const RETRY_FLAG = 'hathor:chunk-retry'

function isChunkError(err: unknown): boolean {
  const msg = err instanceof Error ? err.message : String(err ?? '')
  return /loading chunk \d+ failed|failed to fetch dynamically imported module|importing a module script failed|error loading dynamically/i.test(
    msg,
  )
}

function flagSet(): boolean {
  try {
    return sessionStorage.getItem(RETRY_FLAG) === '1'
  } catch {
    return false
  }
}

function flagClear(): void {
  try {
    sessionStorage.removeItem(RETRY_FLAG)
  } catch {
    /* private mode may reject writes */
  }
}

export function lazyWithRetry(importer: () => Promise<{ default: ComponentType<any> }>) {
  return lazy(async () => {
    try {
      const mod = await importer()
      flagClear()
      return mod
    } catch (err) {
      // Non-chunk errors (syntax/real bugs) go straight to the boundary.
      if (!isChunkError(err) || flagSet()) {
        flagClear()
        throw err
      }
      try {
        sessionStorage.setItem(RETRY_FLAG, '1')
      } catch {
        /* fall through to the boundary below */
        throw err
      }
      window.location.reload()
      // Suspend forever on this render: the reload replaces the page.
      // (If reload is blocked, the pending promise never settles and the
      // Suspense fallback stays — still better than a white screen.)
      return new Promise<{ default: ComponentType<any> }>(() => {})
    }
  })
}
