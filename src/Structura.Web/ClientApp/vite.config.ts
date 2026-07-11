import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'

// Dev: Vite serves the SPA and proxies /api to the ASP.NET Core backend (port 8080).
// Build: output goes straight into the backend's wwwroot so one host serves everything.
export default defineConfig({
  plugins: [
    vue(),
    tailwindcss(),
    VitePWA({
      registerType: 'prompt',
      // The Telegram Mini App and the API must never be intercepted by the service worker.
      injectRegister: null,
      manifest: {
        name: 'Structura',
        short_name: 'Structura',
        description: 'Review extracted records',
        theme_color: '#4f46e5',
        background_color: '#0f1115',
        display: 'standalone',
        start_url: '/review',
        scope: '/',
        icons: [
          { src: '/pwa-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/pwa-512.png', sizes: '512x512', type: 'image/png' },
          { src: '/pwa-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        // Precache the app shell; never precache API responses.
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api/, /^\/hubs/, /^\/tg/],
        runtimeCaching: [],
      },
      devOptions: { enabled: false },
    }),
  ],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8080', changeOrigin: false },
      '/hubs': { target: 'http://localhost:8080', changeOrigin: false, ws: true },
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
})
