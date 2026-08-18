import { API_BASE_URL } from '../lib/env'

// Access token in memory; refresh token persisted so a reload keeps you signed in.
// (Storing the refresh token in localStorage is the common SPA trade-off; a backend
// that issues an httpOnly cookie would be stricter.)
let accessToken: string | null = null

const REFRESH_KEY = 'ww.refreshToken'
const ROLE_KEY = 'ww.role'

export function setAccessToken(token: string | null) {
  accessToken = token
}

export function getRole(): string | null {
  return localStorage.getItem(ROLE_KEY)
}

export function setSession(tokens: { accessToken: string; refreshToken: string; role: string }) {
  accessToken = tokens.accessToken
  localStorage.setItem(REFRESH_KEY, tokens.refreshToken)
  localStorage.setItem(ROLE_KEY, tokens.role)
}

export function clearSession() {
  accessToken = null
  localStorage.removeItem(REFRESH_KEY)
  localStorage.removeItem(ROLE_KEY)
}

/** True while a token refresh is in flight — exercised by the concurrency check. */
export function isRefreshing(): boolean {
  return refreshInFlight !== null
}

export function getRefreshToken(): string | null {
  return localStorage.getItem(REFRESH_KEY)
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function raw(path: string, options: RequestInit): Promise<Response> {
  return fetch(`${API_BASE_URL}${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      ...(options.headers ?? {}),
    },
  })
}

// Refresh tokens rotate on use, so concurrent refreshes must not be allowed.
// The access token lives in memory only, so after a reload every request in
// flight gets a 401 at once. If each one posted its own refresh, the first
// would succeed and rotate the token while the rest presented an
// already-consumed one, failed, and ran clearSession() — tearing down the
// session the first had just established and signing the user out at random.
//
// Callers therefore share a single in-flight refresh and reuse its outcome.
let refreshInFlight: Promise<boolean> | null = null

function tryRefresh(): Promise<boolean> {
  refreshInFlight ??= runRefresh().finally(() => { refreshInFlight = null })
  return refreshInFlight
}

async function runRefresh(): Promise<boolean> {
  const refreshToken = getRefreshToken()
  if (!refreshToken) return false
  const res = await fetch(`${API_BASE_URL}/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken }),
  })
  if (!res.ok) {
    clearSession()
    return false
  }
  const data = await res.json()
  setSession({ accessToken: data.accessToken, refreshToken: data.refreshToken, role: data.role })
  return true
}

export interface RequestOptions {
  method?: string
  body?: unknown
  auth?: boolean
}

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const init: RequestInit = {
    method: options.method ?? 'GET',
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  }

  let res = await raw(path, init)
  if (res.status === 401 && getRefreshToken()) {
    if (await tryRefresh()) {
      res = await raw(path, init)
    }
  }

  if (!res.ok) {
    let message = `Request failed (${res.status})`
    try {
      const data = await res.json()
      if (data?.error) message = data.error
    } catch {
      // no JSON body
    }
    throw new ApiError(res.status, message)
  }

  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}
