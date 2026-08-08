import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, clearSession, getRefreshToken, getRole, setSession } from '../api/client'
import type { LoginResponse } from '../api/types'

interface AuthState {
  role: string | null
  isAuthenticated: boolean
  isAdmin: boolean
  isStaff: boolean
  login: (email: string, password: string) => Promise<LoginResponse>
  completeTwoFactor: (challengeToken: string, code: string) => Promise<void>
  loginWithGoogle: (idToken: string) => Promise<void>
  register: (email: string, password: string) => Promise<void>
  logout: () => void
}

const AuthContext = createContext<AuthState | null>(null)

function persist(data: {
  accessToken?: string
  refreshToken?: string
  role?: string
}) {
  if (data.accessToken && data.refreshToken && data.role) {
    setSession({ accessToken: data.accessToken, refreshToken: data.refreshToken, role: data.role })
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [role, setRole] = useState<string | null>(getRole())

  useEffect(() => {
    // A stored refresh token means a prior session; treat as authenticated until proven otherwise.
    if (getRefreshToken() && !role) setRole(getRole())
  }, [role])

  const value = useMemo<AuthState>(() => ({
    role,
    isAuthenticated: !!getRefreshToken(),
    isAdmin: role === 'Administrator',
    isStaff: role === 'Administrator' || role === 'Manager',
    async login(email, password) {
      const res = await api<LoginResponse>('/auth/login', { method: 'POST', body: { email, password } })
      if (!res.twoFactorRequired) {
        persist(res)
        setRole(res.role ?? null)
      }
      return res
    },
    async completeTwoFactor(challengeToken, code) {
      const res = await api<LoginResponse>('/auth/2fa', { method: 'POST', body: { challengeToken, code } })
      persist(res)
      setRole(res.role ?? null)
    },
    async loginWithGoogle(idToken) {
      const res = await api<LoginResponse>('/auth/google', { method: 'POST', body: { idToken } })
      persist(res)
      setRole(res.role ?? null)
    },
    async register(email, password) {
      await api('/auth/register', { method: 'POST', body: { email, password } })
    },
    logout() {
      clearSession()
      setRole(null)
    },
  }), [role])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
