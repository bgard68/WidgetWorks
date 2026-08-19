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
    expect(JSON.parse(String(quotes.at(-1)?.body ?? quotes.at(-1)?.init?.body))).toMatchObject({ stateCode: 'OR' })
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

  it('offers nothing to check out when the cart is empty', async () => {
    stubFetch([['/cart/', () => ({ ...cart, items: [], itemCount: 0, subtotal: 0 })]])

    renderWithProviders(<CheckoutPage />, { at: '/checkout' })

    expect(await screen.findByText('There is nothing to check out')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Place your order/i })).not.toBeInTheDocument()
  })
})
