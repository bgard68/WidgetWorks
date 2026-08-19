import { render, type RenderResult } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { ReactElement, ReactNode } from 'react'
import { vi } from 'vitest'
import { AuthProvider } from '../auth/AuthContext'
import { CartProvider } from '../cart/CartContext'

export const REFRESH_KEY = 'ww.refreshToken'
export const ROLE_KEY = 'ww.role'
export const CART_KEY = 'ww.cartId'

/** Puts a signed-in session in storage, the way a real login would. */
export function signIn(role: 'Customer' | 'Manager' | 'Administrator' = 'Customer') {
  localStorage.setItem(REFRESH_KEY, 'refresh-token-for-tests')
  localStorage.setItem(ROLE_KEY, role)
}

export function useCartId(id: string) {
  localStorage.setItem(CART_KEY, id)
}

/**
 * Renders a component inside the providers it expects, on a memory router so navigation is
 * observable without a browser. `at` sets the starting URL; any route in `routes` renders a
 * marker so a redirect can be asserted by what lands on screen.
 */
export function renderWithProviders(
  ui: ReactElement,
  { at = '/', routes = {} as Record<string, ReactNode> } = {},
): RenderResult {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <AuthProvider>
        <CartProvider>
          <Routes>
            <Route path={at} element={ui} />
            {Object.entries(routes).map(([path, element]) => (
              <Route key={path} path={path} element={element} />
            ))}
          </Routes>
        </CartProvider>
      </AuthProvider>
    </MemoryRouter>,
  )
}

/**
 * Stubs fetch with a table of [url fragment, responder] pairs. Anything unmatched fails loudly
 * rather than returning undefined — a silent 200-with-nothing hides more bugs than it catches.
 */
export function stubFetch(routes: Array<[string, (init?: RequestInit) => unknown]>) {
  const calls: Array<{ url: string; init?: RequestInit }> = []

  const impl = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })
    const match = routes.find(([fragment]) => url.includes(fragment))
    if (!match) {
      throw new Error(`Unstubbed request: ${init?.method ?? 'GET'} ${url}`)
    }

    const body = match[1](init)
    return new Response(JSON.stringify(body ?? {}), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  vi.stubGlobal('fetch', vi.fn(impl))
  return calls
}
