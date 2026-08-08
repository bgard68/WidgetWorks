import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// VITE_* values are injected at build time from the environment (e.g. GitHub Actions
// Variables/Secrets) — never committed. See .env.example for the variable names.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173 },
})
