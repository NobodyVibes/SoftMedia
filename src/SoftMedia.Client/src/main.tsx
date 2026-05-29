import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './index.css'
import './lib/i18n'
import App from './App.tsx'
import { registerSW } from 'virtual:pwa-register'

// PWA service worker (P2-WI-003). autoUpdate: a new deploy's SW activates on the
// next load and cleans old caches; immediate true so the fresh shell is used at once.
registerSW({ immediate: true })

const queryClient = new QueryClient()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
