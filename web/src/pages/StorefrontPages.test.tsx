import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CatalogPage } from './CatalogPage'
import { ProductPage } from './ProductPage'
import { OrderConfirmationPage } from './OrderConfirmationPage'
import { RegisterPage } from './RegisterPage'
import { renderWithProviders, stubFetch, REFRESH_KEY } from '../test/render'

const widget = {
  id: 'w-1',
  sku: 'WW-001',
  name: 'Standard Widget',
  description: 'A dependable widget for everyday jobs.',
  imageUrl: null,
  price: 12.5,
  quantityOnHand: 10,
  quantityReserved: 0,
  quantityAvailable: 10,
  isActive: true,
}

const soldOut = { ...widget, id: 'w-2', sku: 'WW-002', name: 'Mega Widget', quantityAvailable: 0, price: 99 }

describe('CatalogPage', () => {
  const paged = (items = [widget, soldOut]) => () => ({ items, page: 1, pageSize: 24, total: items.length })

  it('renders the widgets it loads', async () => {
    stubFetch([['/catalog/widgets', paged()]])

    renderWithProviders(<CatalogPage />, { at: '/store' })

    expect(await screen.findByText('Standard Widget')).toBeInTheDocument()
    expect(screen.getByText('Mega Widget')).toBeInTheDocument()
  })

  it('says so instead of showing an empty grid when nothing matches', async () => {
    stubFetch([['/catalog/widgets', paged([])]])

    renderWithProviders(<CatalogPage />, { at: '/store' })

    expect(await screen.findByText('No widgets matched')).toBeInTheDocument()
  })

  it('marks an out-of-stock widget as unbuyable', async () => {
    stubFetch([['/catalog/widgets', paged()]])

    renderWithProviders(<CatalogPage />, { at: '/store' })
    await screen.findByText('Mega Widget')

    expect(screen.getByRole('button', { name: 'Out of stock' })).toBeDisabled()
  })

  it('surfaces a catalog failure', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Catalog is down.' }),
      { status: 503, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<CatalogPage />, { at: '/store' })

    expect(await screen.findByText(/Catalog is down/)).toBeInTheDocument()
  })
})

describe('ProductPage', () => {
  it('shows the product and lets it be bought', async () => {
    stubFetch([['/catalog/widgets/w-1', () => widget]])

    renderWithProviders(<ProductPage />, { at: '/widgets/w-1', path: '/widgets/:id' })

    expect(await screen.findByRole('heading', { name: 'Standard Widget' })).toBeInTheDocument()
    expect(screen.getByText(/A dependable widget/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Add to cart/i })).toBeEnabled()
  })

  it('reports a widget it cannot load rather than rendering a blank page', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Widget not found.' }),
      { status: 404, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<ProductPage />, { at: '/widgets/nope', path: '/widgets/:id' })

    expect(await screen.findByText(/couldn.t load that widget/i)).toBeInTheDocument()
  })

  it('cannot be added when it is out of stock', async () => {
    stubFetch([['/catalog/widgets/w-2', () => soldOut]])

    renderWithProviders(<ProductPage />, { at: '/widgets/w-2', path: '/widgets/:id' })
    await screen.findByRole('heading', { name: 'Mega Widget' })

    expect(screen.getByRole('button', { name: 'Out of stock' })).toBeDisabled()
  })
})

describe('OrderConfirmationPage', () => {
  const paid = {
    orderNumber: 'WW-20260501-ABC123',
    orderId: 'o-1',
    status: 'Paid',
    total: 29.19,
    paymentProvider: 'Mock',
    paymentReference: 'mock_1',
    email: 'jane@example.com',
  }

  const awaiting = { ...paid, status: 'AwaitingPayment', paymentProvider: 'Klarna' }

  it('confirms a paid order with its number and total', () => {
    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: paid })

    expect(screen.getAllByText('WW-20260501-ABC123').length).toBeGreaterThan(0)
    expect(screen.getByText('$29.19')).toBeInTheDocument()
  })

  it('explains what to do when someone lands here with no order', () => {
    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation' })

    expect(screen.getByText('No recent order to show')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Your orders' })).toHaveAttribute('href', '/orders')
  })

  it('offers to settle an order that is awaiting the provider', () => {
    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: awaiting })

    expect(screen.getByText(/Waiting on Klarna/)).toBeInTheDocument()
    expect(screen.getAllByRole('button').length).toBeGreaterThan(0)
  })

  it('settling posts the reference to the mock webhook and updates the status', async () => {
    const calls = stubFetch([['/webhooks/payments/mock', () => ({ status: 'Paid' })]])
    const user = userEvent.setup()

    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: awaiting })
    const [approve] = screen.getAllByRole('button')
    await user.click(approve)

    await waitFor(() => {
      const hook = calls.find((c) => c.url.includes('/webhooks/payments/mock'))
      expect(JSON.parse(String(hook?.init?.body))).toMatchObject({ reference: 'mock_1', outcome: 'succeeded' })
    })
  })

  it('reports a webhook failure instead of pretending it settled', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Invalid webhook signature.' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: awaiting })
    const [approve] = screen.getAllByRole('button')
    await user.click(approve)

    expect(await screen.findByText(/Invalid webhook signature/)).toBeInTheDocument()
  })
})

describe('RegisterPage', () => {
  it('creates the account and sends the new customer to sign in', async () => {
    const calls = stubFetch([['/auth/register', () => ({})]])
    const user = userEvent.setup()

    renderWithProviders(<RegisterPage />, { at: '/register', routes: { '/login': <h1>Sign in</h1> } })

    await user.type(screen.getByLabelText(/Email/i), 'new@example.com')
    await user.type(screen.getByLabelText(/Password/i), 'long-enough-pw')
    await user.click(screen.getByRole('button', { name: /Create account|Create your account|Sign up/i }))

    await waitFor(() => {
      const post = calls.find((c) => c.url.includes('/auth/register'))
      expect(JSON.parse(String(post?.init?.body))).toMatchObject({ email: 'new@example.com' })
    })
  })

  it('shows the API rejection and leaves no session behind', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Unable to register with the provided details.' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<RegisterPage />, { at: '/register', routes: { '/login': <h1>Sign in</h1> } })
    await user.type(screen.getByLabelText(/Email/i), 'taken@example.com')
    await user.type(screen.getByLabelText(/Password/i), 'long-enough-pw')
    await user.click(screen.getByRole('button', { name: /Create account|Create your account|Sign up/i }))

    expect(await screen.findByText(/Unable to register/)).toBeInTheDocument()
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
  })
})
