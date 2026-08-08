// Build-time configuration. Values come from the environment (GitHub Actions
// Variables/Secrets in CI, a git-ignored .env.local in dev) — never committed.
export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5080'
export const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID ?? ''
