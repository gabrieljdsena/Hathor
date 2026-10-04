// Global menu coordinator: only one popup menu may be open at a time.
// Every menu (SongMenu bento/context variants, podcast episode menu)
// claims the bus when it opens; all others close themselves. Closing is
// cooperative — a menu that unmounts releases its claim.
type Listener = (openId: string | null) => void

const listeners = new Set<Listener>()
let openId: string | null = null

export function claimMenu(id: string) {
  openId = id
  for (const l of [...listeners]) l(id)
}

export function releaseMenu(id: string) {
  if (openId !== id) return
  openId = null
  for (const l of [...listeners]) l(null)
}

export function subscribeMenu(fn: Listener): () => void {
  listeners.add(fn)
  return () => {
    listeners.delete(fn)
  }
}
