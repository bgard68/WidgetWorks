// Build-time configuration. Values come from the environment (GitHub Actions
// Variables/Secrets in CI, a git-ignored .env.local in dev) — never committed.
/* v8 ignore next -- a build either has the variable or it does not; one side of each fallback exists per bundle */
export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5080'
/* v8 ignore next -- as above */
export const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID ?? ''
