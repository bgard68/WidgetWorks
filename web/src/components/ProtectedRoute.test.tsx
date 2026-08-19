import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { ProtectedRoute } from './ProtectedRoute'
import { renderWithProviders, signIn } from '../test/render'

/**
 * The client-side half of authorization. It is not the security boundary — the API enforces the
 * real thing — but a hole here shows a signed-out visitor an admin screen, which is its own kind
 * of broken. Every combination of (signed in?, staff route?, role) is checked.
 */
describe('ProtectedRoute', () => {
  const Secret = <h1>Order history</h1>
  const routes = {
    '/login': <h1>Sign in</h1>,
    '/store': <h1>Storefront</h1>,
  }

  it('sends a signed-out visitor to the sign-in page', () => {
    renderWithProviders(<ProtectedRoute>{Secret}</ProtectedRoute>, { at: '/orders', routes })

    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.queryByText('Order history')).not.toBeInTheDocument()
  })

  it('lets a signed-in customer through an ordinary protected route', () => {
    signIn('Customer')
    renderWithProviders(<ProtectedRoute>{Secret}</ProtectedRoute>, { at: '/orders', routes })

    expect(screen.getByRole('heading', { name: 'Order history' })).toBeInTheDocument()
  })

  it('bounces a customer off a staff route to the store, not to sign-in', () => {
    signIn('Customer')
    renderWithProviders(<ProtectedRoute staff>{Secret}</ProtectedRoute>, { at: '/admin/widgets', routes })

    // Signed in but not permitted: sending them to /login would be a confusing dead end.
    expect(screen.getByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
    expect(screen.queryByText('Order history')).not.toBeInTheDocument()
  })

  it.each(['Manager', 'Administrator'] as const)('lets a %s into a staff route', (role) => {
    signIn(role)
    renderWithProviders(<ProtectedRoute staff>{Secret}</ProtectedRoute>, { at: '/admin/widgets', routes })

    expect(screen.getByRole('heading', { name: 'Order history' })).toBeInTheDocument()
  })

  it('sends a signed-out visitor to sign-in even for a staff route', () => {
    renderWithProviders(<ProtectedRoute staff>{Secret}</ProtectedRoute>, { at: '/admin/widgets', routes })

    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('treats a session with a token but no role as not staff', () => {
    // Half-written session: a refresh token without a role must not open admin screens.
    localStorage.setItem('ww.refreshToken', 'refresh-token-for-tests')

    renderWithProviders(<ProtectedRoute staff>{Secret}</ProtectedRoute>, { at: '/admin/widgets', routes })

    expect(screen.getByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
  })
})
