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

async function tryRefresh(): Promise<boolean> {
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
