import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AddToCartButton } from './AddToCartButton'
import { renderWithProviders, stubFetch } from '../test/render'

/**
 * The control every product surface shares. Its whole reason to exist is feedback while the
 * request is in flight, so the states — idle, busy, done, error — are what get asserted, along
 * with the guard that stops a double-click becoming two line items.
 */
describe('AddToCartButton', () => {
  const cart = {
    id: 'cart-1',
    userId: null,
    items: [{ widgetId: 'w-1', sku: 'WW-001', name: 'Standard Widget', unitPrice: 10, quantity: 1, quantityAvailable: 4, lineSubtotal: 10 }],
    subtotal: 10,
    itemCount: 1,
  }

  it('adds the widget and reports success', async () => {
    const calls = stubFetch([['/cart', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" />)
    await user.click(screen.getByRole('button', { name: 'Add to cart' }))

    expect(await screen.findByRole('button', { name: '✓ Added to cart' })).toBeInTheDocument()
    const post = calls.find((c) => c.init?.method === 'POST')
    expect(JSON.parse(String(post?.init?.body))).toMatchObject({ widgetId: 'w-1', quantity: 1 })
  })

  it('sends the quantity it was given', async () => {
    const calls = stubFetch([['/cart', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" quantity={3} />)
    await user.click(screen.getByRole('button', { name: 'Add to cart' }))

    await waitFor(() => {
      const post = calls.find((c) => c.init?.method === 'POST')
      expect(JSON.parse(String(post?.init?.body))).toMatchObject({ quantity: 3 })
    })
  })

  it('ignores a second click while the first is still in flight', async () => {
    // Declared via the resolver rather than a mutable local: TypeScript narrows a variable only
    // assigned inside the executor to `never`, making it uncallable afterwards.
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })

    vi.stubGlobal('fetch', vi.fn(async () => {
      await gate
      return new Response(JSON.stringify(cart), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" />)
    const button = screen.getByRole('button')
    await user.click(button)

    // Disabled while busy: an impatient double-click must not order two.
    expect(await screen.findByRole('button', { name: 'Adding…' })).toBeDisabled()
    await user.click(button)

    release()
    await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1))
  })

  it('shows the reason when the API refuses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Only 2 left in stock.' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" />)
    await user.click(screen.getByRole('button', { name: 'Add to cart' }))

    expect(await screen.findByText('Only 2 left in stock.')).toBeInTheDocument()
  })

  it('is inert and self-explanatory when out of stock', async () => {
    stubFetch([['/cart', () => cart]])
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" outOfStock />)
    const button = screen.getByRole('button', { name: 'Out of stock' })

    expect(button).toBeDisabled()
    await user.click(button)
    expect(screen.queryByText('Adding…')).not.toBeInTheDocument()
  })

  it('honours an explicit disabled without claiming to be out of stock', () => {
    renderWithProviders(<AddToCartButton widgetId="w-1" disabled />)

    expect(screen.getByRole('button', { name: 'Add to cart' })).toBeDisabled()
  })

  it('uses a caller-supplied label', () => {
    renderWithProviders(<AddToCartButton widgetId="w-1" label="Buy now" />)

    expect(screen.getByRole('button', { name: 'Buy now' })).toBeInTheDocument()
  })

  it('notifies the caller after a successful add', async () => {
    stubFetch([['/cart', () => cart]])
    const onAdded = vi.fn()
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" onAdded={onAdded} />)
    await user.click(screen.getByRole('button', { name: 'Add to cart' }))

    await waitFor(() => expect(onAdded).toHaveBeenCalledOnce())
  })

  it('does not notify the caller when the add failed', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'nope' }), { status: 400, headers: { 'Content-Type': 'application/json' } },
    )))
    const onAdded = vi.fn()
    const user = userEvent.setup()

    renderWithProviders(<AddToCartButton widgetId="w-1" onAdded={onAdded} />)
    await user.click(screen.getByRole('button', { name: 'Add to cart' }))

    await screen.findByText('nope')
    expect(onAdded).not.toHaveBeenCalled()
  })
})
