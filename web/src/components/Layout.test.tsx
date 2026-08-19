import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Layout } from './Layout'
import { renderWithProviders, signIn, stubFetch, REFRESH_KEY } from '../test/render'

/**
 * The chrome every page sits in. It is where role leaks would show first: an Admin link visible
 * to a customer, or an order-history link offered to someone with no session.
 */
describe('Layout', () => {
  const render = () => renderWithProviders(<Layout />, { at: '/store' })

  it('offers sign-in and no account links to a visitor', () => {
    stubFetch([['/cart', () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })]])
    render()

    expect(screen.getAllByRole('link', { name: /Sign in/i }).length).toBeGreaterThan(0)
    expect(screen.queryByRole('link', { name: /Admin/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument()
  })

  it('does not show the admin entry point to a customer', () => {
    signIn('Customer')
    stubFetch([['/cart', () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })]])
    render()

    expect(screen.queryByRole('link', { name: /Admin/i })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument()
  })

  it.each(['Manager', 'Administrator'] as const)('shows the admin entry point to a %s', (role) => {
    signIn(role)
    stubFetch([['/cart', () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })]])
    render()

    expect(screen.getByRole('link', { name: /Admin/i })).toBeInTheDocument()
  })

  it('signing out clears the stored session', async () => {
    signIn('Customer')
    stubFetch([['/cart', () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })]])
    const user = userEvent.setup()
    render()

    await user.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
    expect(screen.getAllByRole('link', { name: /Sign in/i }).length).toBeGreaterThan(0)
  })

  it('announces the cart count for screen readers, singular and plural', async () => {
    stubFetch([['/cart', () => ({
      id: 'c',
      userId: null,
      items: [{ widgetId: 'w-1', sku: 'WW-001', name: 'W', unitPrice: 1, quantity: 1, quantityAvailable: 5, lineSubtotal: 1 }],
      subtotal: 1,
      itemCount: 1,
    })]])
    localStorage.setItem('ww.cartId', 'c')
    render()

    expect(await screen.findByRole('link', { name: 'Cart, 1 item' })).toBeInTheDocument()
  })

  it('links back to the demo guide from the promo bar', () => {
    stubFetch([['/cart', () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })]])
    render()

    expect(screen.getByRole('link', { name: /read the guide/i })).toHaveAttribute('href', '/')
  })
})
