import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api } from '../api/client'
import type { CartView } from '../api/types'

const CART_KEY = 'ww.cartId'

interface CartState {
  cart: CartView | null
  itemCount: number
  refresh: () => Promise<void>
  addItem: (widgetId: string, quantity: number) => Promise<void>
  updateItem: (widgetId: string, quantity: number) => Promise<void>
  removeItem: (widgetId: string) => Promise<void>
  clearLocal: () => void
}

const CartContext = createContext<CartState | null>(null)

export function CartProvider({ children }: { children: ReactNode }) {
  const [cart, setCart] = useState<CartView | null>(null)
  const [cartId, setCartId] = useState<string | null>(() => localStorage.getItem(CART_KEY))

  const store = useCallback((next: CartView) => {
    setCart(next)
    setCartId(next.id)
    localStorage.setItem(CART_KEY, next.id)
  }, [])

  const refresh = useCallback(async () => {
    if (!cartId) return
    try {
      const next = await api<CartView>(`/cart/${cartId}`)
      setCart(next)
    } catch {
      localStorage.removeItem(CART_KEY)
      setCartId(null)
      setCart(null)
    }
  }, [cartId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const value = useMemo<CartState>(() => ({
    cart,
    itemCount: cart?.itemCount ?? 0,
    refresh,
    async addItem(widgetId, quantity) {
      const next = await api<CartView>('/cart/items', { method: 'POST', body: { cartId, widgetId, quantity } })
      store(next)
    },
    async updateItem(widgetId, quantity) {
      if (!cartId) return
      const next = await api<CartView>(`/cart/${cartId}/items/${widgetId}`, { method: 'PUT', body: { quantity } })
      store(next)
    },
    async removeItem(widgetId) {
      if (!cartId) return
      const next = await api<CartView>(`/cart/${cartId}/items/${widgetId}`, { method: 'DELETE' })
      store(next)
    },
    clearLocal() {
      localStorage.removeItem(CART_KEY)
      setCartId(null)
      setCart(null)
    },
  }), [cart, cartId, refresh, store])

  return <CartContext.Provider value={value}>{children}</CartContext.Provider>
}

export function useCart(): CartState {
  const ctx = useContext(CartContext)
  if (!ctx) throw new Error('useCart must be used within CartProvider')
  return ctx
}
