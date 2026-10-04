import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import App from './App'
import { AuthProvider } from './auth/AuthContext'
import { api } from './api/client'
import './styles/hathor.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 15_000 } },
})

// Error bridge (desktop js_log + onerror/unhandledrejection forwarders):
// forwards frontend failures to hathor logs via POST /logs/client.
// Logging must never break the app.
function logToServer(message: string) {
  try {
    if (!localStorage.getItem('hathor:token')) return
    if (message.includes('/logs/client')) return // avoid feedback loop
    void api.logClient(message.slice(0, 2000), location.pathname)
  } catch {
    /* logging must never break the app */
  }
}
window.addEventListener('error', (e) => {
  try {
    logToServer(`onerror: ${e.message} @${e.filename}:${e.lineno}:${e.colno}`)
  } catch {
    /* ignore */
  }
})
window.addEventListener('unhandledrejection', (e) => {
  try {
    const r = e.reason as unknown
    const detail = r instanceof Error ? (r.stack ?? r.message) : String(r ?? 'unknown')
    logToServer(`unhandledrejection: ${detail}`)
  } catch {
    /* ignore */
  }
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <App />
        </AuthProvider>
      </QueryClientProvider>
    </BrowserRouter>
  </StrictMode>,
)
