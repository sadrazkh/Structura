import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// Dev: Vite serves the SPA and proxies /api to the ASP.NET Core backend (port 8080).
// Build: output goes straight into the backend's wwwroot so one host serves everything.
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8080', changeOrigin: false },
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
})
