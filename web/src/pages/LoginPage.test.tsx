import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { LoginPage } from './LoginPage'
import { renderWithProviders, stubFetch, stubFetchRejecting, useCartId, REFRESH_KEY, ROLE_KEY } from '../test/render'

// Google sign-in is offered only when a client id is configured, and the SDK itself arrives from
// a script tag. Both are stubbed so the page's Google branch can be driven.
vi.mock('../lib/env', () => ({ GOOGLE_CLIENT_ID: 'client-123', API_BASE_URL: 'http://localhost:5080' }))

let googleCallback: ((r: { credential: string }) => void) | null = null

// Re-stubbed per test: the suite runs with unstubGlobals, which clears it after each one.
beforeEach(() => {
  vi.stubGlobal('google', {
    accounts: {
      id: {
        initialize: (c: { callback: (r: { credential: string }) => void }) => { googleCallback = c.callback },
        renderButton: () => {},
      },
    },
  })
})

afterEach(() => {
  googleCallback = null
  document.getElementById('google-identity')?.remove()
})

/**
 * Sign-in, including the two-step branch and the guest-cart merge. The merge is the subtle one:
 * a shopper who filled a basket before signing in must not lose it, and a merge failure must not
 * strand them on the sign-in page after their credentials were accepted.
 */
describe('LoginPage', () => {
  const session = { accessToken: 'access', refreshToken: 'refresh', role: 'Customer', twoFactorRequired: false }

  const routes = { '/store': <h1>Storefront</h1> }

  async function signInAs(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('Email address'), 'jane@example.com')
    await user.type(screen.getByLabelText('Password'), 'correct-horse')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
  }

  it('stores the session and lands on the store', async () => {
    stubFetch([['/auth/login', () => session]])
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    expect(await screen.findByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
    expect(localStorage.getItem(REFRESH_KEY)).toBe('refresh')
    expect(localStorage.getItem(ROLE_KEY)).toBe('Customer')
  })

  it('sends the typed credentials, not something stale', async () => {
    const calls = stubFetch([['/auth/login', () => session]])
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    await waitFor(() => {
      const login = calls.find((c) => c.url.includes('/auth/login'))
      expect(JSON.parse(String(login?.init?.body))).toEqual({ email: 'jane@example.com', password: 'correct-horse' })
    })
  })

  it('shows the failure and stays put when credentials are rejected', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Invalid email or password.' }),
      { status: 401, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Storefront' })).not.toBeInTheDocument()
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
  })

  it('asks for the second factor instead of signing in, and stores nothing yet', async () => {
    stubFetch([['/auth/login', () => ({ twoFactorRequired: true, challengeToken: 'challenge-1' })]])
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    expect(await screen.findByRole('heading', { name: 'Two-step verification' })).toBeInTheDocument()

    // A password alone must not leave a usable session behind.
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
    expect(screen.queryByRole('heading', { name: 'Storefront' })).not.toBeInTheDocument()
  })

  it('completes the second factor with the challenge it was handed', async () => {
    const calls = stubFetch([
      ['/auth/2fa', () => session],
      ['/auth/login', () => ({ twoFactorRequired: true, challengeToken: 'challenge-1' })],
    ])
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    await user.type(await screen.findByLabelText('Verification code'), '654321')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    expect(await screen.findByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
    const second = calls.find((c) => c.url.includes('/auth/2fa'))
    expect(JSON.parse(String(second?.init?.body))).toEqual({ challengeToken: 'challenge-1', code: '654321' })
  })

  it('reports a wrong code without losing the challenge', async () => {
    let calls = 0
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/auth/2fa')) {
        calls++
        return new Response(JSON.stringify({ error: 'Invalid code.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify({ twoFactorRequired: true, challengeToken: 'challenge-1' }), {
        status: 200, headers: { 'Content-Type': 'application/json' },
      })
    }))
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)
    await user.type(await screen.findByLabelText('Verification code'), '000000')
    await user.click(screen.getByRole('button', { name: 'Verify' }))

    expect(await screen.findByText('Invalid code.')).toBeInTheDocument()

    // Still on the code step, so a second attempt does not need a fresh password.
    expect(screen.getByRole('heading', { name: 'Two-step verification' })).toBeInTheDocument()
    expect(calls).toBe(1)
  })

  it('merges a guest cart into the account on the way in', async () => {
    useCartId('cart-1')
    const calls = stubFetch([
      ['/cart/merge', () => ({ id: 'cart-1', userId: 'u-1', items: [], subtotal: 0, itemCount: 0 })],
      ['/auth/login', () => session],
      ['/cart/cart-1', () => ({ id: 'cart-1', userId: null, items: [], subtotal: 0, itemCount: 0 })],
    ])
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    await waitFor(() => {
      const merge = calls.find((c) => c.url.includes('/cart/merge'))
      expect(JSON.parse(String(merge?.init?.body))).toEqual({ guestCartId: 'cart-1' })
    })
    expect(await screen.findByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
  })

  it('still signs in when the cart merge fails', async () => {
    useCartId('cart-1')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/cart/merge')) {
        return new Response(JSON.stringify({ error: 'merge blew up' }), {
          status: 500, headers: { 'Content-Type': 'application/json' },
        })
      }
      if (url.includes('/auth/login')) {
        return new Response(JSON.stringify(session), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      return new Response(JSON.stringify({ id: 'cart-1', userId: null, items: [], subtotal: 0, itemCount: 0 }), {
        status: 200, headers: { 'Content-Type': 'application/json' },
      })
    }))
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)

    // Credentials were accepted; a basket problem must not undo that.
    expect(await screen.findByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
    expect(localStorage.getItem(REFRESH_KEY)).toBe('refresh')
  })

  it('offers the way out for a forgotten password and a new account', () => {
    renderWithProviders(<LoginPage />, { at: '/login', routes })

    expect(screen.getByRole('link', { name: /Forgot your password/i })).toHaveAttribute('href', '/forgot-password')
    expect(screen.getByRole('link', { name: /Create an account/i })).toHaveAttribute('href', '/register')
  })

  it.each([
    ['sign in', 'Sign in failed.'],
    ['the second factor', 'Invalid code.'],
  ])('falls back to its own message when %s fails with a non-Error', async (step, message) => {
    const twoFactor = step === 'the second factor'
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (twoFactor && url.includes('/auth/login')) {
        return new Response(JSON.stringify({ twoFactorRequired: true, challengeToken: 'challenge-1' }), {
          status: 200, headers: { 'Content-Type': 'application/json' },
        })
      }
      throw 'network exploded'
    }))
    const user = userEvent.setup()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await signInAs(user)
    if (twoFactor) {
      await user.type(await screen.findByLabelText('Verification code'), '654321')
      await user.click(screen.getByRole('button', { name: 'Verify' }))
    }

    expect(await screen.findByText(message)).toBeInTheDocument()
  })

  it('falls back to its own message when Google sign-in fails with a non-Error', async () => {
    stubFetchRejecting()

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await deliverGoogleCredential('google-id-token')

    expect(await screen.findByText('Google sign-in failed.')).toBeInTheDocument()
  })

  it('signs in with a Google credential and lands on the store', async () => {
    const calls = stubFetch([['/auth/google', () => session]])

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await deliverGoogleCredential('google-id-token')

    expect(await screen.findByRole('heading', { name: 'Storefront' })).toBeInTheDocument()
    const post = calls.find((c) => c.url.includes('/auth/google'))
    expect(JSON.parse(String(post?.init?.body))).toEqual({ idToken: 'google-id-token' })
    expect(localStorage.getItem(REFRESH_KEY)).toBe('refresh')
  })

  it('reports a refused Google credential without leaving the page', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Your Google email address is not verified.' }),
      { status: 401, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<LoginPage />, { at: '/login', routes })
    await deliverGoogleCredential('unverified-token')

    expect(await screen.findByText(/not verified/)).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Storefront' })).not.toBeInTheDocument()
  })
})

/**
 * Drives the Google button the way the SDK does: the page hands GoogleButton a callback, the
 * mocked SDK captures it on initialize, and this fires it with a credential.
 */
async function deliverGoogleCredential(credential: string) {
  const script = document.getElementById('google-identity') as HTMLScriptElement | null
  script?.onload?.(new Event('load'))
  await act(async () => {
    googleCallback?.({ credential })
  })
}
