import { Navigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import type { ReactNode } from 'react'

export function ProtectedRoute({ children, staff }: { children: ReactNode; staff?: boolean }) {
  const { isAuthenticated, isStaff } = useAuth()
  if (!isAuthenticated) return <Navigate to="/login" replace />
  if (staff && !isStaff) return <Navigate to="/" replace />
  return <>{children}</>
}
