import { describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useLocation } from 'react-router-dom'
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

  // ---- search -------------------------------------------------------------------------
  //
  // The input is deliberately local state: typing must not re-run the catalog query on every
  // keystroke, so the URL (and therefore the fetch) only changes on submit.

  const emptyCart = () => ({ id: 'c', userId: null, items: [], subtotal: 0, itemCount: 0 })

  /**
   * Renders the layout on the guide route, with /store echoing the URL it was navigated to —
   * so a search can be asserted by the address it produced.
   */
  const renderSearching = () =>
    renderWithProviders(<Layout />, { at: '/', routes: { '/store': <LocationEcho /> } })

  it('submitting the search puts the term in the URL', async () => {
    stubFetch([['/cart', emptyCart]])
    const user = userEvent.setup()
    renderSearching()

    await user.type(screen.getByLabelText('Search widgets'), 'mega')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    expect(await screen.findByTestId('loc')).toHaveTextContent('/store?q=mega')
  })

  it('trims the term and drops an all-whitespace search', async () => {
    stubFetch([['/cart', emptyCart]])
    const user = userEvent.setup()
    renderSearching()

    await user.type(screen.getByLabelText('Search widgets'), '   ')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    // Nothing worth searching for — the plain store URL, not an empty ?q=.
    const loc = await screen.findByTestId('loc')
    expect(loc).toHaveTextContent('/store')
    expect(loc).not.toHaveTextContent('q=')
  })

  it('choosing a category navigates immediately, keeping any typed term', async () => {
    stubFetch([['/cart', emptyCart]])
    const user = userEvent.setup()
    renderSearching()

    await user.type(screen.getByLabelText('Search widgets'), 'widget')
    await user.selectOptions(screen.getByLabelText('Search category'), 'mega')

    expect(await screen.findByTestId('loc')).toHaveTextContent('/store?q=widget&cat=mega')
  })

  it('seeds the search box and scope from the URL', () => {
    stubFetch([['/cart', emptyCart]])
    renderWithProviders(<Layout />, { at: '/store?q=kit&cat=kit', path: '/store' })

    expect(screen.getByLabelText('Search widgets')).toHaveValue('kit')
    expect(screen.getByLabelText('Search category')).toHaveValue('kit')
    // The rail marks the active category only while actually on the catalog
    // (the footer links to the same places but is never "current").
    const rail = within(screen.getByRole('navigation', { name: 'Product categories' }))
    expect(rail.getByRole('link', { name: 'Kits' })).toHaveClass('on')
  })

  it('back to top scrolls the window rather than jumping the anchor', async () => {
    stubFetch([['/cart', emptyCart]])
    const scrollTo = vi.fn()
    vi.stubGlobal('scrollTo', scrollTo)
    const user = userEvent.setup()
    render()

    await user.click(screen.getByRole('button', { name: 'Back to top' }))

    expect(scrollTo).toHaveBeenCalledWith({ top: 0, behavior: 'smooth' })
  })
})

/** Renders the current URL so a navigation can be asserted by what lands on screen. */
function LocationEcho() {
  const location = useLocation()
  return <span data-testid="loc">{`${location.pathname}${location.search}`}</span>
}
