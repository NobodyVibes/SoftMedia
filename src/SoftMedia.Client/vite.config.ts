import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { VitePWA } from 'vite-plugin-pwa'
import path from 'path'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [
    react(),
    // PWA shell (P2-WI-003). NOT added to vitest.config.ts on purpose, so the SW is
    // never generated during unit tests. Media/proxy/cache are explicitly EXCLUDED
    // from precache and runtime caching — they must never be served stale.
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['vite.svg', 'icons/icon-192.png', 'icons/icon-512.png', 'icons/icon-512-maskable.png'],
      manifest: {
        name: 'SoftMedia',
        short_name: 'SoftMedia',
        description: 'Your self-hosted media server.',
        theme_color: '#007AFF',
        background_color: '#0f172a',
        display: 'standalone',
        start_url: '/',
        scope: '/',
        icons: [
          { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: '/icons/icon-512-maskable.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        // Precache ONLY the built app shell (JS/CSS/HTML/icons). Media, transcode
        // segments, the image proxy, and the image cache are never precached and —
        // because we declare no runtimeCaching — never runtime-cached either, so they
        // always hit the network fresh.
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        // The app-shell JS bundle is ~2.5 MB; raise the precache cap so it's cached
        // for offline. (A future code-split would shrink this; not in scope here.)
        maximumFileSizeToCacheInBytes: 5 * 1024 * 1024,
        // SPA navigation fallback to index.html for offline route loads, but NOT for
        // API/media/cache/hub paths (those must reach the network, not the shell).
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/cache\//, /^\/hubs\//],
      },
    }),
  ],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    host: '0.0.0.0',
    proxy: {
      '/api': {
        target: 'http://localhost:5011',
        changeOrigin: true,
        secure: false,
      },
      '/cache': {
        target: 'http://localhost:5011',
        changeOrigin: true,
        secure: false,
      },
      '/hubs': {
        target: 'http://localhost:5011',
        changeOrigin: true,
        secure: false,
        ws: true, // Enable WebSocket proxying for SignalR
      },
    },
  },
})
