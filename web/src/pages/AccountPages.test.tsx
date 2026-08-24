import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ForgotPasswordPage } from './ForgotPasswordPage'
import { ResetPasswordPage } from './ResetPasswordPage'
import { OrderDetailPage } from './OrderDetailPage'
import { renderWithProviders, signIn, stubFetch, stubFetchRejecting } from '../test/render'

describe('ForgotPasswordPage', () => {
  it('sends the address and confirms without revealing whether it exists', async () => {
    const calls = stubFetch([['/auth/forgot-password', () => ({})]])
    const user = userEvent.setup()

    renderWithProviders(<ForgotPasswordPage />, { at: '/forgot-password' })
    await user.type(screen.getByLabelText('Email address'), 'jane@example.com')
    await user.click(screen.getByRole('button', { name: /Send|Reset|Email/i }))

    await waitFor(() => {
      const post = calls.find((c) => c.url.includes('/auth/forgot-password'))
      expect(JSON.parse(String(post?.init?.body))).toEqual({ email: 'jane@example.com' })
    })
  })

  it('answers identically for an unknown address', async () => {
    // Account enumeration guard: a failure must look exactly like a success.
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'no such user' }),
      { status: 404, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<ForgotPasswordPage />, { at: '/forgot-password' })
    await user.type(screen.getByLabelText('Email address'), 'nobody@example.com')
    await user.click(screen.getByRole('button', { name: /Send|Reset|Email/i }))

    await waitFor(() => expect(screen.queryByText(/no such user/i)).not.toBeInTheDocument())
  })
})

describe('ResetPasswordPage', () => {
  it('refuses to submit without a token in the link', () => {
    renderWithProviders(<ResetPasswordPage />, { at: '/reset-password' })

    expect(screen.getByRole('button', { name: /Reset|Save|Change/i })).toBeDisabled()
  })

  it('posts the token from the query string with the new password', async () => {
    const calls = stubFetch([['/auth/reset-password', () => ({})]])
    const user = userEvent.setup()

    renderWithProviders(<ResetPasswordPage />, { at: '/reset-password?token=tok-123', path: '/reset-password' })
    await user.type(screen.getByLabelText(/New password/), 'a-brand-new-password')
    await user.click(screen.getByRole('button', { name: /Reset|Save|Change/i }))

    await waitFor(() => {
      const post = calls.find((c) => c.url.includes('/auth/reset-password'))
      expect(JSON.parse(String(post?.init?.body))).toEqual({ token: 'tok-123', newPassword: 'a-brand-new-password' })
    })
  })

  it('shows an expired or used token as the reason', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'That reset link has expired.' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<ResetPasswordPage />, { at: '/reset-password?token=stale', path: '/reset-password' })
    await user.type(screen.getByLabelText(/New password/), 'a-brand-new-password')
    await user.click(screen.getByRole('button', { name: /Reset|Save|Change/i }))

    expect(await screen.findByText(/That reset link has expired/)).toBeInTheDocument()
  })

  it('falls back to its own message when the failure is not an Error', async () => {
    stubFetchRejecting()
    const user = userEvent.setup()

    renderWithProviders(<ResetPasswordPage />, { at: '/reset-password?token=tok-123', path: '/reset-password' })
    await user.type(screen.getByLabelText(/New password/), 'a-brand-new-password')
    await user.click(screen.getByRole('button', { name: /Reset|Save|Change/i }))

    expect(await screen.findByText('Reset failed.')).toBeInTheDocument()
  })
})

describe('OrderDetailPage', () => {
  const order = {
    id: 'o-1',
    orderNumber: 'WW-20260501-ABC123',
    status: 'Paid',
    email: 'jane@example.com',
    subtotal: 20,
    shippingMethod: 'Standard',
    shipping: 7.74,
    taxState: 'CA',
    taxRate: 0.0725,
    tax: 1.45,
    total: 29.19,
    paymentProvider: 'Mock',
    paymentReference: 'mock_1',
    trackingNumber: '1Z999AA10123456784',
    createdAt: '2026-05-01T08:00:00Z',
    items: [{ widgetId: 'w-1', sku: 'WW-001', name: 'Standard Widget', unitPrice: 10, quantity: 2, lineSubtotal: 20 }],
  }

  it('is the receipt: every money line and the tracking number', async () => {
    signIn('Customer')
    stubFetch([['/orders/o-1', () => order]])

    renderWithProviders(<OrderDetailPage />, { at: '/orders/o-1', path: '/orders/:id' })

    expect((await screen.findAllByText(/WW-20260501-ABC123/)).length).toBeGreaterThan(0)
    // $20.00 is both the line subtotal and the order subtotal.
    expect(screen.getAllByText('$20.00').length).toBeGreaterThan(0)
    expect(screen.getByText('$7.74')).toBeInTheDocument()
    expect(screen.getByText('$1.45')).toBeInTheDocument()
    expect(screen.getByText('$29.19')).toBeInTheDocument()
    expect(screen.getByText('1Z999AA10123456784')).toBeInTheDocument()
    expect(screen.getByText('Standard Widget')).toBeInTheDocument()
  })

  it('reports an order it cannot load', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Order not found.' }),
      { status: 404, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<OrderDetailPage />, { at: '/orders/nope', path: '/orders/:id' })

    expect(await screen.findByText(/Order not found/)).toBeInTheDocument()
  })

  it('offers a printable receipt', async () => {
    signIn('Customer')
    stubFetch([['/orders/o-1', () => order]])
    const print = vi.fn()
    vi.stubGlobal('print', print)
    const user = userEvent.setup()

    renderWithProviders(<OrderDetailPage />, { at: '/orders/o-1', path: '/orders/:id' })
    await user.click(await screen.findByRole('button', { name: /Print/i }))

    expect(print).toHaveBeenCalled()
  })

  it('shows free shipping as FREE and omits payment detail an order has none of', async () => {
    signIn('Customer')
    stubFetch([['/orders/o-2', () => ({
      ...order,
      id: 'o-2',
      shipping: 0,
      taxState: '',
      trackingNumber: null,
      paymentProvider: null,
      paymentReference: null,
    })]])

    renderWithProviders(<OrderDetailPage />, { at: '/orders/o-2', path: '/orders/:id' })

    expect(await screen.findByText('FREE')).toBeInTheDocument()
    expect(screen.queryByText(/Paid with/)).not.toBeInTheDocument()
    expect(screen.queryByText('1Z999AA10123456784')).not.toBeInTheDocument()
  })

  it('names the payment provider without a reference when there is none', async () => {
    signIn('Customer')
    stubFetch([['/orders/o-3', () => ({ ...order, id: 'o-3', paymentReference: null })]])

    renderWithProviders(<OrderDetailPage />, { at: '/orders/o-3', path: '/orders/:id' })

    const note = await screen.findByText(/Paid with Mock/)
    expect(note).toBeInTheDocument()
    expect(note).not.toHaveTextContent('ref')
  })

  it('shows a skeleton while the order is loading', () => {
    signIn('Customer')
    stubFetch([['/orders/o-1', () => order]])

    renderWithProviders(<OrderDetailPage />, { at: '/orders/o-1', path: '/orders/:id' })

    expect(screen.getByRole('status', { name: 'Loading' })).toBeInTheDocument()
  })

  it('asks for nothing when the URL carries no order id', () => {
    signIn('Customer')
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)

    renderWithProviders(<OrderDetailPage />, { at: '/orders', path: '/orders' })

    expect(fetchMock).not.toHaveBeenCalled()
  })
})
