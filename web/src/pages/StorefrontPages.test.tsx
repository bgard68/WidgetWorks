import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CatalogPage } from './CatalogPage'
import { ProductPage } from './ProductPage'
import { OrderConfirmationPage } from './OrderConfirmationPage'
import { RegisterPage } from './RegisterPage'
import { renderWithProviders, stubFetch, stubFetchRejecting, REFRESH_KEY } from '../test/render'

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

  it('leads with the hero while browsing, and swaps it for a breadcrumb once refined', async () => {
    stubFetch([['/catalog/widgets', paged()]])

    const { unmount } = renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })
    expect(await screen.findByRole('heading', { name: /Widgets for every job/i })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Breadcrumb' })).not.toBeInTheDocument()
    unmount()

    stubFetch([['/catalog/widgets', paged()]])
    renderWithProviders(<CatalogPage />, { at: '/store?cat=mega', path: '/store' })

    expect(await screen.findByRole('navigation', { name: 'Breadcrumb' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Mega widgets' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: /Widgets for every job/i })).not.toBeInTheDocument()
  })

  it('titles a search by its term and sends it to the API', async () => {
    const calls = stubFetch([['/catalog/widgets', paged()]])

    renderWithProviders(<CatalogPage />, { at: '/store?q=mega', path: '/store' })

    expect(await screen.findByRole('heading', { name: /Results for/ })).toBeInTheDocument()
    await waitFor(() => expect(calls.some((c) => c.url.includes('search=mega'))).toBe(true))
  })

  it('counts what is shown, in the singular when only one matched', async () => {
    // The API returns the narrowed set now, so the fixture is the answer to
    // ?category=mega rather than something the page filters afterwards.
    stubFetch([['/catalog/widgets', paged([soldOut])]])

    renderWithProviders(<CatalogPage />, { at: '/store?cat=mega', path: '/store' })

    expect(await screen.findByText('1 product')).toBeInTheDocument()
  })

  it('says how many the page was narrowed from when the API has more', async () => {
    stubFetch([['/catalog/widgets', () => ({ items: [widget, soldOut], page: 1, pageSize: 24, totalCount: 40 })]])

    renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })

    // The grid shows one page; the count must not pretend the catalog is only
    // as big as that page.
    expect(await screen.findByText('2 products of 40')).toBeInTheDocument()
  })

  it('asks the API to re-order rather than sorting the page it already has', async () => {
    const calls = stubFetch([['/catalog/widgets', paged()]])
    const user = userEvent.setup()

    renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })
    await screen.findByText('Standard Widget')

    await user.selectOptions(screen.getByLabelText('Sort by'), 'price-desc')

    // Sorting in the browser would only order whatever happened to be on this
    // page. The request carries the choice so the ordering applies to the whole
    // matching set.
    await waitFor(() => expect(calls.some((c) => c.url.includes('sort=price-desc'))).toBe(true))
  })

  it('narrows a category through the API, not in the browser', async () => {
    const calls = stubFetch([['/catalog/widgets', paged()]])

    renderWithProviders(<CatalogPage />, { at: '/store?cat=mega', path: '/store' })
    await screen.findByRole('heading', { name: 'Mega widgets' })

    await waitFor(() => expect(calls.some((c) => c.url.includes('category=mega'))).toBe(true))
  })

  it('clears a category from the toolbar', async () => {
    stubFetch([['/catalog/widgets', paged()]])
    const user = userEvent.setup()

    renderWithProviders(<CatalogPage />, { at: '/store?cat=mega', path: '/store' })
    await screen.findByRole('heading', { name: 'Mega widgets' })

    await user.click(screen.getByRole('button', { name: /Clear category/ }))

    // Back to browsing everything, hero and all.
    expect(await screen.findByRole('heading', { name: 'Featured widgets' })).toBeInTheDocument()
    expect(screen.getByText('Standard Widget')).toBeInTheDocument()
  })

  it('explains an empty search differently from an empty category', async () => {
    stubFetch([['/catalog/widgets', paged([])]])
    const { unmount } = renderWithProviders(<CatalogPage />, { at: '/store?q=nothing', path: '/store' })
    expect(await screen.findByText(/couldn.t find anything for/i)).toBeInTheDocument()
    unmount()

    stubFetch([['/catalog/widgets', paged([])]])
    renderWithProviders(<CatalogPage />, { at: '/store?cat=mega', path: '/store' })
    expect(await screen.findByText(/Nothing in this category yet/)).toBeInTheDocument()
  })

  it('flags a low-stock widget and marks which lines ship free', async () => {
    const low = { ...widget, id: 'w-3', sku: 'WW-003', name: 'Mini Widget', quantityAvailable: 4, price: 80 }
    stubFetch([['/catalog/widgets', paged([low])]])

    renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })

    expect(await screen.findByText('Only 4 left')).toBeInTheDocument()
    expect(screen.getByText('FREE shipping')).toBeInTheDocument()
  })

  it('nudges toward free shipping on a cheaper widget', async () => {
    stubFetch([['/catalog/widgets', paged([widget])]])

    renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })

    expect(await screen.findByText(/Free shipping over \$75/)).toBeInTheDocument()
  })

  it('leaves a well-stocked widget unflagged', async () => {
    stubFetch([['/catalog/widgets', paged([{ ...widget, quantityAvailable: 250 }])]])

    renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })
    await screen.findByText('Standard Widget')

    // Badges are for exceptions; plenty in stock is not one.
    expect(screen.queryByText(/left$/)).not.toBeInTheDocument()
    expect(screen.queryByText('Out of stock')).not.toBeInTheDocument()
  })

  it('names the category in an empty search result', async () => {
    stubFetch([['/catalog/widgets', paged([])]])

    renderWithProviders(<CatalogPage />, { at: '/store?q=zzz&cat=mega', path: '/store' })

    expect(await screen.findByText(/in Mega/)).toBeInTheDocument()
  })

  it('drops a response that arrives after the shopper has gone', async () => {
    let release: (() => void) | undefined
    vi.stubGlobal('fetch', vi.fn(async () => {
      await new Promise<void>((resolve) => { release = resolve })
      return new Response(JSON.stringify({ items: [widget], page: 1, pageSize: 24, totalCount: 1 }), {
        status: 200, headers: { 'Content-Type': 'application/json' },
      })
    }))

    const { unmount } = renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })
    unmount()
    release?.()

    // A setState after unmount is the classic React warning; the effect's `active`
    // flag exists to prevent it.
    await waitFor(() => expect(screen.queryByText('Standard Widget')).not.toBeInTheDocument())
  })

  it('drops a failure that arrives after the shopper has gone', async () => {
    let reject: ((reason: unknown) => void) | undefined
    vi.stubGlobal('fetch', vi.fn(() => new Promise((_resolve, rej) => { reject = rej })))

    const { unmount } = renderWithProviders(<CatalogPage />, { at: '/store', path: '/store' })
    unmount()
    reject?.(new Error('Catalog is down.'))

    // The same guard on the unhappy path: no error banner rendered into a page
    // nobody is looking at.
    await waitFor(() => expect(screen.queryByText('Catalog is down.')).not.toBeInTheDocument())
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

  it('offers no quantity picker for something that cannot be bought', async () => {
    stubFetch([['/catalog/widgets/w-2', () => soldOut]])

    renderWithProviders(<ProductPage />, { at: '/widgets/w-2', path: '/widgets/:id' })
    await screen.findByRole('heading', { name: 'Mega Widget' })

    expect(screen.queryByLabelText('Qty')).not.toBeInTheDocument()
    expect(screen.getByText('Currently unavailable')).toBeInTheDocument()
    // Said in both places a shopper looks: the buy box and the specs table.
    expect(screen.getAllByText('Out of stock')).toHaveLength(2)
  })

  it('caps the quantity picker at ten, or at what is left', async () => {
    stubFetch([['/catalog/widgets/w-3', () => ({ ...widget, id: 'w-3', quantityAvailable: 3 })]])

    renderWithProviders(<ProductPage />, { at: '/widgets/w-3', path: '/widgets/:id' })

    const qty = await screen.findByLabelText('Qty')
    expect(within(qty).getAllByRole('option').map((o) => o.textContent)).toEqual(['1', '2', '3'])
    expect(screen.getByText('Only 3 left in stock')).toBeInTheDocument()
  })

  it('recalculates free shipping as the quantity changes', async () => {
    // 12.50 each: one is under the $75 threshold, six are over it.
    stubFetch([['/catalog/widgets/w-1', () => widget]])
    const user = userEvent.setup()

    renderWithProviders(<ProductPage />, { at: '/widgets/w-1', path: '/widgets/:id' })
    await screen.findByRole('heading', { name: 'Standard Widget' })
    // Scoped to the buy box: the same promise is repeated in the page's USP list.
    const buybox = within(screen.getByRole('complementary', { name: 'Purchase options' }))
    expect(buybox.getByText(/Free standard shipping on orders over/)).toBeInTheDocument()

    await user.selectOptions(screen.getByLabelText('Qty'), '6')

    expect(await buybox.findByText(/standard shipping on this order/)).toBeInTheDocument()
  })

  it('confirms an add with a link straight to the cart', async () => {
    stubFetch([
      ['/cart/items', () => ({ id: 'c-1', userId: null, items: [], subtotal: 0, itemCount: 1 })],
      ['/catalog/widgets/w-1', () => widget],
    ])
    const user = userEvent.setup()

    renderWithProviders(<ProductPage />, { at: '/widgets/w-1', path: '/widgets/:id' })
    await screen.findByRole('heading', { name: 'Standard Widget' })
    await user.click(screen.getByRole('button', { name: /Add to cart/i }))

    expect(await screen.findByRole('link', { name: 'view cart' })).toHaveAttribute('href', '/cart')
  })

  it('shows a skeleton rather than a blank frame while loading', () => {
    stubFetch([['/catalog/widgets/w-1', () => widget]])

    renderWithProviders(<ProductPage />, { at: '/widgets/w-1', path: '/widgets/:id' })

    expect(screen.getByRole('status', { name: 'Loading' })).toBeInTheDocument()
  })

  it('reports plain availability when stock is healthy', async () => {
    stubFetch([['/catalog/widgets/w-4', () => ({ ...widget, id: 'w-4', quantityAvailable: 250 })]])

    renderWithProviders(<ProductPage />, { at: '/widgets/w-4', path: '/widgets/:id' })

    expect(await screen.findByText('In stock')).toBeInTheDocument()
    expect(screen.getByText('250 available')).toBeInTheDocument()
    // Ten is the most anyone can put in one basket.
    expect(within(screen.getByLabelText('Qty')).getAllByRole('option')).toHaveLength(10)
  })

  it('asks for nothing when the URL carries no widget id', () => {
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<ProductPage />, { at: '/widgets', path: '/widgets' })

    expect(fetchMock).not.toHaveBeenCalled()
    expect(screen.getByRole('status', { name: 'Loading' })).toBeInTheDocument()
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

  it('simulating a decline turns the page into a payment failure', async () => {
    const calls = stubFetch([['/webhooks/payments/mock', () => ({ status: 'PaymentFailed' })]])
    const user = userEvent.setup()

    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: awaiting })
    await user.click(screen.getByRole('button', { name: /Simulate decline/i }))

    expect(await screen.findByRole('heading', { name: 'Payment not completed' })).toBeInTheDocument()
    expect(screen.getByText(/order was cancelled and your items released/)).toBeInTheDocument()
    const hook = calls.find((c) => c.url.includes('/webhooks/payments/mock'))
    expect(JSON.parse(String(hook?.init?.body))).toMatchObject({ outcome: 'failed' })
  })

  it('confirms a paid order without repeating the email when none was captured', () => {
    const { email: _email, ...noEmail } = paid
    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: noEmail })

    expect(screen.getByRole('heading', { name: /your order is confirmed/i })).toBeInTheDocument()
    expect(screen.queryByText(/confirmation sent to/)).not.toBeInTheDocument()
  })

  it('reports a settlement failure that is not an Error', async () => {
    stubFetchRejecting()
    const user = userEvent.setup()

    renderWithProviders(<OrderConfirmationPage />, { at: '/order-confirmation', state: awaiting })
    const [approve] = screen.getAllByRole('button')
    await user.click(approve)

    expect(await screen.findByText('Could not settle the payment.')).toBeInTheDocument()
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

  it('signs the new customer straight in and sends them to the store', async () => {
    const calls = stubFetch([
      ['/auth/register', () => ({})],
      ['/auth/login', () => ({ accessToken: 'a', refreshToken: 'r', role: 'Customer' })],
    ])
    const user = userEvent.setup()

    renderWithProviders(<RegisterPage />, { at: '/register', routes: { '/store': <h1>Storefront</h1> } })
    await user.type(screen.getByLabelText(/Email/i), 'new@example.com')
    await user.type(screen.getByLabelText(/Password/i), 'long-enough-pw')
    await user.click(screen.getByRole('button', { name: /Create account|Create your account|Sign up/i }))

    // Registering then being asked to log in again would be a pointless second step.
    expect(await screen.findByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
    expect(calls.some((c) => c.url.includes('/auth/login'))).toBe(true)
    expect(localStorage.getItem(REFRESH_KEY)).toBe('r')
  })

  it('falls back to its own message when the failure is not an Error', async () => {
    stubFetchRejecting()
    const user = userEvent.setup()

    renderWithProviders(<RegisterPage />, { at: '/register', routes: { '/login': <h1>Sign in</h1> } })
    await user.type(screen.getByLabelText(/Email/i), 'new@example.com')
    await user.type(screen.getByLabelText(/Password/i), 'long-enough-pw')
    await user.click(screen.getByRole('button', { name: /Create account|Create your account|Sign up/i }))

    expect(await screen.findByText('Registration failed.')).toBeInTheDocument()
  })
})
