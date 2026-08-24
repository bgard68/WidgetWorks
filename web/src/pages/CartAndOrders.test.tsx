import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CartPage } from './CartPage'
import { OrdersPage } from './OrdersPage'
import { AdminOrderPage } from './admin/AdminOrderPage'
import { renderWithProviders, signIn, stubFetch, useCartId } from '../test/render'

const line = {
  widgetId: 'w-1',
  sku: 'WW-001',
  name: 'Standard Widget',
  unitPrice: 10,
  quantity: 2,
  quantityAvailable: 5,
  lineSubtotal: 20,
}

const cart = { id: 'cart-1', userId: null, items: [line], subtotal: 20, itemCount: 2 }

describe('CartPage', () => {
  it('lists what is in the basket', async () => {
    useCartId('cart-1')
    stubFetch([['/cart/cart-1', () => cart]])

    renderWithProviders(<CartPage />, { at: '/cart' })

    expect(await screen.findByText('Standard Widget')).toBeInTheDocument()
    expect(screen.getByText('$20.00')).toBeInTheDocument()
  })

  it('increasing a quantity sends the new absolute quantity, not a delta', async () => {
    useCartId('cart-1')
    const calls = stubFetch([['/cart/cart-1', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<CartPage />, { at: '/cart' })
    await user.click(await screen.findByRole('button', { name: 'Increase quantity of Standard Widget' }))

    await waitFor(() => {
      const put = calls.find((c) => c.init?.method === 'PUT')
      expect(JSON.parse(String(put?.init?.body))).toMatchObject({ quantity: 3 })
    })
  })

  it('decreasing a quantity sends one fewer', async () => {
    useCartId('cart-1')
    const calls = stubFetch([['/cart/cart-1', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<CartPage />, { at: '/cart' })
    await user.click(await screen.findByRole('button', { name: 'Decrease quantity of Standard Widget' }))

    await waitFor(() => {
      const put = calls.find((c) => c.init?.method === 'PUT')
      expect(JSON.parse(String(put?.init?.body))).toMatchObject({ quantity: 1 })
    })
  })

  it('removing a line calls DELETE for that widget', async () => {
    useCartId('cart-1')
    const calls = stubFetch([['/cart/cart-1', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<CartPage />, { at: '/cart' })
    await user.click(await screen.findByRole('button', { name: 'Remove' }))

    await waitFor(() => {
      const del = calls.find((c) => c.init?.method === 'DELETE')
      expect(del?.url).toContain('w-1')
    })
  })

  it('sends the shopper to checkout', async () => {
    useCartId('cart-1')
    stubFetch([['/cart/cart-1', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<CartPage />, { at: '/cart', routes: { '/checkout': <h1>Secure checkout</h1> } })
    await user.click(await screen.findByRole('button', { name: /Proceed to checkout|Checkout/i }))

    expect(await screen.findByRole('heading', { name: 'Secure checkout' })).toBeInTheDocument()
  })

  it('offers a way back to the store when empty', async () => {
    stubFetch([['/cart/', () => ({ ...cart, items: [], itemCount: 0, subtotal: 0 })]])

    renderWithProviders(<CartPage />, { at: '/cart' })

    expect(await screen.findByText('Your cart is empty')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Start shopping' })).toHaveAttribute('href', '/store')
  })

  it('shows why an update failed instead of silently ignoring it', async () => {
    useCartId('cart-1')
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        return new Response(JSON.stringify({ error: 'This widget is out of stock.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CartPage />, { at: '/cart' })
    await user.click(await screen.findByRole('button', { name: 'Increase quantity of Standard Widget' }))

    expect(await screen.findByText('This widget is out of stock.')).toBeInTheDocument()
  })

  it('will not push a line past what is in stock', async () => {
    useCartId('cart-1')
    // Two in the basket, two available: there is nothing left to add.
    stubFetch([['/cart/cart-1', () => ({ ...cart, items: [{ ...line, quantityAvailable: 2 }] })]])

    renderWithProviders(<CartPage />, { at: '/cart' })

    expect(await screen.findByRole('button', { name: 'Increase quantity of Standard Widget' })).toBeDisabled()
  })

  it('says how much more would earn free shipping, then that it is earned', async () => {
    useCartId('cart-1')
    stubFetch([['/cart/cart-1', () => cart]])   // $20 subtotal, under the $75 threshold
    const { unmount } = renderWithProviders(<CartPage />, { at: '/cart' })

    expect(await screen.findByText(/more to qualify for free standard shipping/)).toBeInTheDocument()
    expect(screen.getByText('$55.00')).toBeInTheDocument()
    unmount()

    localStorage.clear()
    useCartId('cart-1')
    stubFetch([['/cart/cart-1', () => ({ ...cart, subtotal: 90, items: [{ ...line, quantity: 9, lineSubtotal: 90 }], itemCount: 9 })]])
    renderWithProviders(<CartPage />, { at: '/cart' })

    expect(await screen.findByText(/qualifies for/)).toBeInTheDocument()
  })

  it('falls back to its own message when the failure is not an Error', async () => {
    useCartId('cart-1')
    const first = { ...cart }
    let calls = 0
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'PUT') throw 'network exploded'
      calls++
      return new Response(JSON.stringify(first), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<CartPage />, { at: '/cart' })
    await user.click(await screen.findByRole('button', { name: 'Increase quantity of Standard Widget' }))

    expect(await screen.findByText('Could not update the cart.')).toBeInTheDocument()
    expect(calls).toBeGreaterThan(0)
  })

  it('counts a single item in the singular', async () => {
    useCartId('cart-1')
    stubFetch([['/cart/cart-1', () => ({ ...cart, itemCount: 1, subtotal: 10, items: [{ ...line, quantity: 1, lineSubtotal: 10 }] })]])

    renderWithProviders(<CartPage />, { at: '/cart' })

    expect(await screen.findByText('1 item')).toBeInTheDocument()
  })
})

describe('OrdersPage', () => {
  const order = {
    id: 'o-1',
    orderNumber: 'WW-20260501-ABC123',
    status: 'Paid',
    total: 29.19,
    itemCount: 2,
    createdAt: '2026-05-01T08:00:00Z',
  }

  it('lists the account orders with their status', async () => {
    signIn('Customer')
    stubFetch([['/orders', () => [order]]])

    renderWithProviders(<OrdersPage />, { at: '/orders' })

    expect(await screen.findByText('WW-20260501-ABC123')).toBeInTheDocument()
    expect(screen.getByText('$29.19')).toBeInTheDocument()
    expect(screen.getByText('Paid')).toBeInTheDocument()
  })

  it('says so plainly when there are none', async () => {
    signIn('Customer')
    stubFetch([['/orders', () => []]])

    renderWithProviders(<OrdersPage />, { at: '/orders' })

    expect(await screen.findByText('No orders yet')).toBeInTheDocument()
  })

  it('surfaces a load failure rather than an endless skeleton', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Orders unavailable.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<OrdersPage />, { at: '/orders' })

    expect(await screen.findByText(/Orders unavailable/)).toBeInTheDocument()
  })
})

describe('AdminOrderPage', () => {
  const summary = {
    id: 'o-1',
    orderNumber: 'WW-20260501-ABC123',
    status: 'Paid',
    total: 29.19,
    itemCount: 2,
    createdAt: '2026-05-01T08:00:00Z',
  }

  const detail = {
    ...summary,
    email: 'jane@example.com',
    subtotal: 20,
    shippingMethod: 'Standard',
    shipping: 7.74,
    taxState: 'CA',
    taxRate: 0.0725,
    tax: 1.45,
    paymentProvider: 'Mock',
    paymentReference: 'mock_1',
    trackingNumber: null,
    items: [{ widgetId: 'w-1', sku: 'WW-001', name: 'Standard Widget', unitPrice: 10, quantity: 2, lineSubtotal: 20 }],
  }

  it('lists recent orders so staff can find one without knowing its id', async () => {
    signIn('Manager')
    stubFetch([['/admin/orders', () => [summary]]])

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })

    // The regression this page exists for: lookup used to require a GUID nobody has.
    expect(await screen.findByText('WW-20260501-ABC123')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open' })).toBeInTheDocument()
  })

  it('opening an order shows its detail and fulfilment controls', async () => {
    signIn('Manager')
    stubFetch([
      ['/admin/orders/o-1', () => detail],
      ['/admin/orders', () => [summary]],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))

    expect(await screen.findByText('jane@example.com')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Mark shipped' })).toBeInTheDocument()
  })

  it('marking shipped posts the status with the tracking number typed in', async () => {
    signIn('Manager')
    const calls = stubFetch([
      ['/admin/orders/o-1/status', () => ({ ...detail, status: 'Shipped', trackingNumber: '1Z-NEW' })],
      ['/admin/orders/o-1', () => detail],
      ['/admin/orders', () => [summary]],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))
    await user.type(await screen.findByLabelText('Tracking number'), '1Z-NEW')
    await user.click(screen.getByRole('button', { name: 'Mark shipped' }))

    await waitFor(() => {
      const post = calls.find((c) => c.url.includes('/status'))
      expect(JSON.parse(String(post?.init?.body))).toEqual({ status: 'Shipped', trackingNumber: '1Z-NEW' })
    })
  })

  it('sends null rather than an empty string when no tracking was entered', async () => {
    signIn('Manager')
    const calls = stubFetch([
      ['/admin/orders/o-1/status', () => ({ ...detail, status: 'Cancelled' })],
      ['/admin/orders/o-1', () => detail],
      ['/admin/orders', () => [summary]],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    await waitFor(() => {
      const post = calls.find((c) => c.url.includes('/status'))
      expect(JSON.parse(String(post?.init?.body))).toEqual({ status: 'Cancelled', trackingNumber: null })
    })
  })

  it('shows the API refusal when a transition is not allowed', async () => {
    signIn('Manager')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/status')) {
        return new Response(JSON.stringify({ error: "Cannot change status from AwaitingPayment to 'Shipped'." }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      if (url.includes('/admin/orders/o-1')) {
        return new Response(JSON.stringify(detail), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      return new Response(JSON.stringify([summary]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))
    await user.click(await screen.findByRole('button', { name: 'Mark shipped' }))

    expect(await screen.findByText(/Cannot change status/)).toBeInTheDocument()
  })

  it('says there is nothing to fulfil when the list is empty', async () => {
    signIn('Manager')
    stubFetch([['/admin/orders', () => []]])

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })

    expect(await screen.findByText('No orders yet')).toBeInTheDocument()
  })

  it('refreshes the list on demand', async () => {
    signIn('Manager')
    const calls = stubFetch([['/admin/orders', () => [summary]]])
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await screen.findByText('WW-20260501-ABC123')
    const before = calls.filter((c) => c.url.includes('/admin/orders')).length

    await user.click(screen.getByRole('button', { name: 'Refresh' }))

    await waitFor(() => expect(calls.filter((c) => c.url.includes('/admin/orders')).length).toBeGreaterThan(before))
  })

  it('prompts staff to pick an order before showing controls', async () => {
    signIn('Manager')
    stubFetch([['/admin/orders', () => [summary]]])

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })

    const aside = await screen.findByText('No order selected')
    expect(within(aside.closest('.panel') as HTMLElement).getByText(/Pick an order/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Mark shipped' })).not.toBeInTheDocument()
  })

  it('reports an order it cannot open', async () => {
    signIn('Manager')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/admin/orders/o-1')) {
        return new Response(JSON.stringify({ error: 'Order not found.' }), {
          status: 404, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify([summary]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))

    expect(await screen.findByText('Order not found.')).toBeInTheDocument()
    expect(screen.getByText('No order selected')).toBeInTheDocument()
  })

  it('shows a tracking number the order already carries', async () => {
    signIn('Manager')
    stubFetch([
      ['/admin/orders/o-1', () => ({ ...detail, status: 'Shipped', trackingNumber: '1Z-EXISTING' })],
      ['/admin/orders', () => [summary]],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))

    // Pre-filled, so re-saving a status does not wipe the number already sent to the customer.
    expect(await screen.findByLabelText('Tracking number')).toHaveValue('1Z-EXISTING')
  })

  it('counts a single order in the singular', async () => {
    signIn('Manager')
    stubFetch([['/admin/orders', () => [summary]]])

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })

    expect(await screen.findByText(/1 most recent order\./)).toBeInTheDocument()
  })

  it('reports a list failure rather than loading forever', async () => {
    signIn('Manager')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Orders unavailable.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })

    expect(await screen.findByText('Orders unavailable.')).toBeInTheDocument()
  })

  it('marks an order delivered', async () => {
    signIn('Manager')
    const calls = stubFetch([
      ['/admin/orders/o-1/status', () => ({ ...detail, status: 'Delivered' })],
      ['/admin/orders/o-1', () => ({ ...detail, status: 'Shipped', trackingNumber: '1Z-EXISTING' })],
      ['/admin/orders', () => [summary]],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))
    await user.click(await screen.findByRole('button', { name: 'Delivered' }))

    await waitFor(() => {
      const post = calls.find((c) => c.url.includes('/status'))
      expect(JSON.parse(String(post?.init?.body))).toEqual({ status: 'Delivered', trackingNumber: '1Z-EXISTING' })
    })
  })

  it('falls back to its own messages when failures are not Errors', async () => {
    signIn('Manager')
    let listed = false
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/admin/orders/o-1')) throw 'network exploded'
      listed = true
      return new Response(JSON.stringify([summary]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))

    expect(await screen.findByText('Could not load that order.')).toBeInTheDocument()
    expect(listed).toBe(true)
  })

  it('falls back to its own message when a status update is not an Error', async () => {
    signIn('Manager')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/status')) throw 'network exploded'
      if (url.includes('/admin/orders/o-1')) {
        return new Response(JSON.stringify(detail), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      return new Response(JSON.stringify([summary]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminOrderPage />, { at: '/admin/orders' })
    await user.click(await screen.findByRole('button', { name: 'Open' }))
    await user.click(await screen.findByRole('button', { name: 'Mark shipped' }))

    expect(await screen.findByText('Update failed.')).toBeInTheDocument()
  })
})
