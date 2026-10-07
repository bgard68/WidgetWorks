import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CheckoutPage } from './CheckoutPage'
import { renderWithProviders, stubFetch, useCartId } from '../test/render'

/**
 * The screen where a mistake costs money. Three things are worth guarding: the totals shown come
 * from the server and are re-fetched when the inputs that change them change; the selected payment
 * method is the one actually submitted; and a decline leaves the shopper on the page with the
 * reason, rather than dropping them somewhere with an empty cart.
 */
describe('CheckoutPage', () => {
  const cart = {
    id: 'cart-1',
    userId: null,
    items: [{ widgetId: 'w-1', sku: 'WW-001', name: 'Standard Widget', unitPrice: 10, quantity: 2, quantityAvailable: 5, lineSubtotal: 20 }],
    subtotal: 20,
    itemCount: 2,
  }

  const quoteFor = (state: string, method: string) => ({
    subtotal: 20,
    shippingMethod: method,
    shipping: method === 'Express' ? 21.49 : 7.74,
    stateCode: state,
    taxRate: state === 'CA' ? 0.0725 : 0,
    tax: state === 'CA' ? 1.45 : 0,
    total: 20 + (method === 'Express' ? 21.49 : 7.74) + (state === 'CA' ? 1.45 : 0),
    itemCount: 2,
    isEmpty: false,
  })

  function stubCheckout(onCheckout?: (init?: RequestInit) => unknown) {
    return stubFetch([
      ['/checkout/quote', (init) => {
        const body = JSON.parse(String(init?.body))
        return quoteFor(body.stateCode, body.shippingMethod)
      }],
      ['/checkout', onCheckout ?? (() => ({
        orderNumber: 'WW-20260501-ABC123',
        orderId: 'o-1',
        status: 'Paid',
        total: 29.19,
        paymentProvider: 'Mock',
        paymentReference: 'mock_1',
      }))],
      [`/cart/cart-1`, () => cart],
    ])
  }

  // The page offers the same submit twice — inline under the form and in the sticky summary.
  const placeOrderButton = () => screen.getAllByRole('button', { name: /Place your order/i })[0]

  async function fillAddress(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('Email address'), 'jane@example.com')
    await user.type(screen.getByLabelText('Full name'), 'Jane Doe')
    await user.type(screen.getByLabelText('Address line 1'), '1 Main St')
    await user.type(screen.getByLabelText('City'), 'Springfield')
    await user.type(screen.getByLabelText('ZIP code'), '90210')
  }

  it('shows the server-calculated totals rather than adding up in the browser', async () => {
    useCartId('cart-1')
    stubCheckout()

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })

    expect(await screen.findByText('$29.19')).toBeInTheDocument()   // 20 + 7.74 + 1.45
    expect(screen.getByText(/7\.25% CA/)).toBeInTheDocument()
    expect(screen.getByText('$1.45')).toBeInTheDocument()
  })

  it('re-quotes when the destination state changes', async () => {
    useCartId('cart-1')
    const calls = stubCheckout()
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })
    await screen.findByText('$29.19')

    await user.selectOptions(screen.getByLabelText('State'), 'OR')

    // Oregon has no sales tax, so the total must drop — and it must come from a new quote call.
    await waitFor(() => expect(screen.getByText('$27.74')).toBeInTheDocument())
    const quotes = calls.filter((c) => c.url.includes('/checkout/quote'))
    expect(quotes.length).toBeGreaterThan(1)
    // Indexed rather than .at(-1): that is an ES2022 API and the project targets ES2020.
    const latest = quotes[quotes.length - 1]
    expect(JSON.parse(String(latest.init?.body))).toMatchObject({ stateCode: 'OR' })
  })

  it('re-quotes when the shipping method changes', async () => {
    useCartId('cart-1')
    const calls = stubCheckout()
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })
    await screen.findByText('$29.19')

    await user.click(screen.getByLabelText(/Express shipping/))

    await waitFor(() => expect(screen.getByText('$42.94')).toBeInTheDocument())
    expect(calls.filter((c) => c.url.includes('/checkout/quote')).length).toBeGreaterThan(1)
  })

  it('submits the token for the payment method the shopper picked', async () => {
    useCartId('cart-1')
    const calls = stubCheckout()
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')

    await fillAddress(user)
    await user.click(screen.getByLabelText(/Klarna/))
    await user.click(placeOrderButton())

    await waitFor(() => {
      const order = calls.find((c) => c.init?.method === 'POST' && c.url.endsWith('/checkout'))
      expect(JSON.parse(String(order?.init?.body))).toMatchObject({
        paymentToken: 'klarna_demo',
        email: 'jane@example.com',
        state: 'CA',
      })
    })
  })

  it('defaults to the card token when nothing is picked', async () => {
    useCartId('cart-1')
    const calls = stubCheckout()
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')

    await fillAddress(user)
    await user.click(placeOrderButton())

    await waitFor(() => {
      const order = calls.find((c) => c.init?.method === 'POST' && c.url.endsWith('/checkout'))
      expect(JSON.parse(String(order?.init?.body))).toMatchObject({ paymentToken: 'tok_visa_ok' })
    })
  })

  it('moves to the confirmation page once the order is placed', async () => {
    useCartId('cart-1')
    stubCheckout()
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')

    await fillAddress(user)
    await user.click(placeOrderButton())

    expect(await screen.findByRole('heading', { name: 'Thank you' })).toBeInTheDocument()
  })

  it('keeps the shopper on the page with the reason when payment is declined', async () => {
    useCartId('cart-1')
    stubFetch([
      ['/checkout/quote', (init) => {
        const body = JSON.parse(String(init?.body))
        return quoteFor(body.stateCode, body.shippingMethod)
      }],
      ['/cart/cart-1', () => cart],
    ])
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')
    await fillAddress(user)

    // Swap in a declining gateway only for the order call.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/checkout') && init?.method === 'POST') {
        return new Response(JSON.stringify({ error: 'Your card was declined.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
        status: 200, headers: { 'Content-Type': 'application/json' },
      })
    }))

    await user.click(placeOrderButton())

    expect(await screen.findByText('Your card was declined.')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Thank you' })).not.toBeInTheDocument()
  })

  it('sends an Idempotency-Key so a retried submit cannot place a second order', async () => {
    useCartId('cart-1')
    const calls = stubCheckout()
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')
    await fillAddress(user)
    await user.click(placeOrderButton())

    await screen.findByRole('heading', { name: 'Thank you' })

    const order = calls.find((c) => c.url.endsWith('/checkout') && c.init?.method === 'POST')
    const key = (order?.init?.headers as Record<string, string>)['Idempotency-Key']
    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i)
  })

  it('retries an interrupted submit under the same key rather than a fresh one', async () => {
    useCartId('cart-1')
    const keys: string[] = []
    let attempt = 0

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/checkout') && init?.method === 'POST') {
        keys.push((init.headers as Record<string, string>)['Idempotency-Key'])
        attempt += 1
        // The first attempt dies on the wire: the shopper never learns whether it landed, which is
        // exactly when sending the same key again has to be safe.
        if (attempt === 1) throw new Error('network exploded')
        return new Response(JSON.stringify({
          orderNumber: 'WW-20260501-ABC123', orderId: 'o-1', status: 'Paid',
          total: 29.19, paymentProvider: 'Mock', paymentReference: 'mock_1',
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      if (url.includes('/checkout/quote')) {
        return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')
    await fillAddress(user)

    await user.click(placeOrderButton())
    expect(await screen.findByText('network exploded')).toBeInTheDocument()

    await user.click(placeOrderButton())
    await screen.findByRole('heading', { name: 'Thank you' })

    // Same key both times — the server can recognise the second as a retry of the first.
    expect(keys).toHaveLength(2)
    expect(keys[0]).toBe(keys[1])
  })

  it('waits out an in-flight conflict instead of showing the shopper an error', async () => {
    useCartId('cart-1')
    const keys: string[] = []
    let attempt = 0

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/checkout') && init?.method === 'POST') {
        keys.push((init.headers as Record<string, string>)['Idempotency-Key'])
        attempt += 1
        // Another copy of this request got there first and is still at the gateway.
        if (attempt === 1) {
          return new Response(JSON.stringify({
            error: 'A checkout with this Idempotency-Key is still being processed. Retry shortly.',
            code: 'checkout_in_flight',
          }), { status: 409, headers: { 'Content-Type': 'application/json' } })
        }
        return new Response(JSON.stringify({
          orderNumber: 'WW-20260501-ABC123', orderId: 'o-1', status: 'Paid',
          total: 29.19, paymentProvider: 'Mock', paymentReference: 'mock_1',
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      if (url.includes('/checkout/quote')) {
        return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')
    await fillAddress(user)
    await user.click(placeOrderButton())

    // One click, and the shopper lands on the confirmation: the conflict was absorbed.
    expect(await screen.findByRole('heading', { name: 'Thank you' })).toBeInTheDocument()
    expect(keys).toHaveLength(2)
    expect(keys[0]).toBe(keys[1])
  })

  it('does not retry a conflict that waiting cannot fix', async () => {
    useCartId('cart-1')
    let attempts = 0

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/checkout') && init?.method === 'POST') {
        attempts += 1
        return new Response(JSON.stringify({
          error: 'This Idempotency-Key was already used for a different request.',
          code: 'idempotency_key_reused',
        }), { status: 409, headers: { 'Content-Type': 'application/json' } })
      }
      if (url.includes('/checkout/quote')) {
        return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })
    await screen.findByText('$29.19')
    await fillAddress(user)
    await user.click(placeOrderButton())

    expect(await screen.findByText(/already used for a different request/)).toBeInTheDocument()
    expect(attempts).toBe(1)   // surfaced at once rather than retried three more times for nothing
  })

  it('gives up and says so when the first attempt never finishes', async () => {
    useCartId('cart-1')
    let attempts = 0

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/checkout') && init?.method === 'POST') {
        attempts += 1
        return new Response(JSON.stringify({
          error: 'A checkout with this Idempotency-Key is still being processed. Retry shortly.',
          code: 'checkout_in_flight',
        }), { status: 409, headers: { 'Content-Type': 'application/json' } })
      }
      if (url.includes('/checkout/quote')) {
        return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })
    await screen.findByText('$29.19')
    await fillAddress(user)
    await user.click(placeOrderButton())

    // Four tries in total, then the honest message — better than a spinner that never stops.
    expect(await screen.findByText(/still being processed/, undefined, { timeout: 5000 })).toBeInTheDocument()
    expect(attempts).toBe(4)
  })

  it('retires the key once the server has refused the attempt on its merits', async () => {
    useCartId('cart-1')
    const keys: string[] = []
    let attempt = 0

    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.endsWith('/checkout') && init?.method === 'POST') {
        keys.push((init.headers as Record<string, string>)['Idempotency-Key'])
        attempt += 1
        if (attempt === 1) {
          return new Response(JSON.stringify({ error: 'Your card was declined.' }), {
            status: 400, headers: { 'Content-Type': 'application/json' },
          })
        }
        return new Response(JSON.stringify({
          orderNumber: 'WW-20260501-ABC123', orderId: 'o-1', status: 'Paid',
          total: 29.19, paymentProvider: 'Mock', paymentReference: 'mock_1',
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      if (url.includes('/checkout/quote')) {
        return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout', routes: { '/order-confirmation': <h1>Thank you</h1> } })
    await screen.findByText('$29.19')
    await fillAddress(user)

    await user.click(placeOrderButton())
    expect(await screen.findByText('Your card was declined.')).toBeInTheDocument()

    // Trying another card is a different request. Reusing the key here would answer it with the
    // decline the first attempt earned, and the shopper could never recover.
    await user.click(placeOrderButton())
    await screen.findByRole('heading', { name: 'Thank you' })

    expect(keys).toHaveLength(2)
    expect(keys[0]).not.toBe(keys[1])
  })

  it('reports a quote failure instead of showing a stale or invented total', async () => {
    useCartId('cart-1')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/checkout/quote')) {
        return new Response(JSON.stringify({ error: 'Cart not found.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })

    expect(await screen.findByText('Cart not found.')).toBeInTheDocument()
    // The summary stays in its "calculating" state rather than inventing figures.
    expect(screen.queryByText('Order total')).not.toBeInTheDocument()
  })

  it('falls back to its own message when checkout fails with a non-Error', async () => {
    useCartId('cart-1')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/checkout/quote')) {
        return new Response(JSON.stringify(quoteFor('CA', 'Standard')), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      if (url.includes('/checkout')) throw 'network exploded'
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })
    await screen.findByText('$29.19')

    await fillAddress(user)
    await user.click(placeOrderButton())

    expect(await screen.findByText('Checkout failed.')).toBeInTheDocument()
  })

  it('shows free shipping as FREE rather than $0.00', async () => {
    useCartId('cart-1')
    stubFetch([
      ['/checkout/quote', () => ({
        subtotal: 90, shippingMethod: 'Standard', shipping: 0, stateCode: 'CA',
        taxRate: 0.0725, tax: 6.53, total: 96.53, itemCount: 2, isEmpty: false,
      })],
      ['/cart/cart-1', () => cart],
    ])

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })

    expect(await screen.findByText('FREE')).toBeInTheDocument()
    expect(screen.getByText('$96.53')).toBeInTheDocument()
  })

  it('offers nothing to check out when the cart is empty', async () => {
    stubFetch([['/cart/', () => ({ ...cart, items: [], itemCount: 0, subtotal: 0 })]])

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })

    expect(await screen.findByText('There is nothing to check out')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Place your order/i })).not.toBeInTheDocument()
  })
})
