import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CartProvider, useCart } from './CartContext'
import { CART_KEY, stubFetch, useCartId } from '../test/render'

/**
 * The cart store, driven through a probe. The pages cover add/update/remove
 * against a live cart; this covers the store's own edges — a stored cart id
 * that no longer resolves, mutations with no cart at all, and the provider
 * guard.
 */
function Probe() {
  const cart = useCart()
  return (
    <>
      <span data-testid="count">{cart.itemCount}</span>
      <span data-testid="cart">{cart.cart?.id ?? 'none'}</span>
      <button onClick={() => void cart.updateItem('w-1', 3)}>update</button>
      <button onClick={() => void cart.removeItem('w-1')}>remove</button>
      <button onClick={cart.clearLocal}>clear</button>
    </>
  )
}

const renderProbe = () => render(<CartProvider><Probe /></CartProvider>)

describe('CartContext', () => {
  it('forgets a stored cart the API no longer recognises', async () => {
    useCartId('gone-cart')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Cart not found.' }),
      { status: 404, headers: { 'Content-Type': 'application/json' } },
    )))

    renderProbe()

    // The stale id is dropped so the next add starts a fresh cart, instead of
    // every page load failing on a cart that was consumed by a checkout.
    await waitFor(() => expect(localStorage.getItem(CART_KEY)).toBeNull())
    expect(screen.getByTestId('cart')).toHaveTextContent('none')
    expect(screen.getByTestId('count')).toHaveTextContent('0')
  })

  it('mutations without a cart are quiet no-ops', async () => {
    const calls = stubFetch([])   // any request would fail the test loudly
    const user = userEvent.setup()
    renderProbe()

    await user.click(screen.getByRole('button', { name: 'update' }))
    await user.click(screen.getByRole('button', { name: 'remove' }))

    expect(calls).toHaveLength(0)
    expect(screen.getByTestId('count')).toHaveTextContent('0')
  })

  it('clearLocal drops the cart and its stored id', async () => {
    useCartId('cart-1')
    stubFetch([['/cart/cart-1', () => ({ id: 'cart-1', userId: null, items: [], subtotal: 10, itemCount: 2 })]])
    const user = userEvent.setup()
    renderProbe()

    await waitFor(() => expect(screen.getByTestId('cart')).toHaveTextContent('cart-1'))
    await user.click(screen.getByRole('button', { name: 'clear' }))

    expect(screen.getByTestId('cart')).toHaveTextContent('none')
    expect(localStorage.getItem(CART_KEY)).toBeNull()
  })

  it('refuses to be used outside its provider', () => {
    const quiet = vi.spyOn(console, 'error').mockImplementation(() => {})
    try {
      expect(() => render(<Probe />)).toThrow(/within CartProvider/)
    } finally {
      quiet.mockRestore()
    }
  })
})
