import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The debug REST API lives on the CLI's HttpListener (default port 5050).
// Proxy /api so the dev server can talk to it same-origin.
export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5050',
        changeOrigin: true,
      },
    },
  },
})
