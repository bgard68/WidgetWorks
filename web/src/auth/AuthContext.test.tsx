import { describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AuthProvider, useAuth } from './AuthContext'
import { REFRESH_KEY, ROLE_KEY, stubFetch } from '../test/render'

/**
 * The auth store itself, driven through a probe component. The pages already
 * exercise login and 2FA end to end; what lives here is the rest of the
 * contract — Google sign-in, the refusal to persist half a session, and the
 * guard against using the hook outside its provider.
 */
function Probe() {
  const auth = useAuth()
  return (
    <>
      <span data-testid="state">{`${auth.isAuthenticated}|${auth.role}|${auth.isAdmin}|${auth.isStaff}`}</span>
      <button onClick={() => void auth.login('jane@example.com', 'correct-horse')}>login</button>
      <button onClick={() => void auth.completeTwoFactor('challenge-1', '654321')}>2fa</button>
      <button onClick={() => void auth.loginWithGoogle('google-id-token')}>google</button>
      <button onClick={() => void auth.register('new@example.com', 'long-enough-pw')}>register</button>
      <button onClick={auth.logout}>logout</button>
    </>
  )
}

const renderProbe = () => render(<AuthProvider><Probe /></AuthProvider>)

describe('AuthContext', () => {
  it('signs in with Google and stores the whole session', async () => {
    const calls = stubFetch([
      ['/auth/google', () => ({ accessToken: 'a', refreshToken: 'r', role: 'Customer' })],
    ])
    const user = userEvent.setup()
    renderProbe()

    expect(screen.getByTestId('state')).toHaveTextContent('false|null')
    await user.click(screen.getByRole('button', { name: 'google' }))

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('true|Customer|false|false'))
    expect(localStorage.getItem(REFRESH_KEY)).toBe('r')
    const post = calls.find((c) => c.url.includes('/auth/google'))
    expect(JSON.parse(String(post?.init?.body))).toEqual({ idToken: 'google-id-token' })
  })

  it('never persists a partial session', async () => {
    // A malformed response (no tokens) must leave storage untouched rather than
    // store an unusable half-session that looks signed-in.
    stubFetch([['/auth/google', () => ({})]])
    const user = userEvent.setup()
    renderProbe()

    await user.click(screen.getByRole('button', { name: 'google' }))

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('false|null'))
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
    expect(localStorage.getItem(ROLE_KEY)).toBeNull()
  })

  it.each([
    ['login', '/auth/login'],
    ['2fa', '/auth/2fa'],
  ])('treats a response with no role as no role at all (%s)', async (button, path) => {
    // A response the client cannot make a session out of must leave the app signed
    // out, not signed in as `undefined` — which would render as a staff-less limbo.
    stubFetch([[path, () => ({ twoFactorRequired: false })]])
    const user = userEvent.setup()
    renderProbe()

    await user.click(screen.getByRole('button', { name: button }))

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('false|null|false|false'))
    expect(localStorage.getItem(ROLE_KEY)).toBeNull()
  })

  it('a two-factor challenge stores nothing until the code is accepted', async () => {
    stubFetch([['/auth/login', () => ({ twoFactorRequired: true, challengeToken: 'challenge-1' })]])
    const user = userEvent.setup()
    renderProbe()

    await user.click(screen.getByRole('button', { name: 'login' }))

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('false|null'))
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
  })

  it('register posts the details without creating a session by itself', async () => {
    const calls = stubFetch([['/auth/register', () => ({})]])
    const user = userEvent.setup()
    renderProbe()

    await user.click(screen.getByRole('button', { name: 'register' }))

    await waitFor(() => expect(calls.some((c) => c.url.includes('/auth/register'))).toBe(true))
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
  })

  it('marks Administrator and Manager as staff, and only Administrator as admin', async () => {
    localStorage.setItem(REFRESH_KEY, 'r')
    localStorage.setItem(ROLE_KEY, 'Administrator')
    renderProbe()
    expect(screen.getByTestId('state')).toHaveTextContent('true|Administrator|true|true')

    localStorage.setItem(ROLE_KEY, 'Manager')
    render(<AuthProvider><Probe /></AuthProvider>)
    expect(screen.getAllByTestId('state')[1]).toHaveTextContent('true|Manager|false|true')
  })

  it('logout clears everything at once', async () => {
    localStorage.setItem(REFRESH_KEY, 'r')
    localStorage.setItem(ROLE_KEY, 'Customer')
    const user = userEvent.setup()
    renderProbe()

    await user.click(screen.getByRole('button', { name: 'logout' }))

    expect(screen.getByTestId('state')).toHaveTextContent('false|null')
    expect(localStorage.getItem(REFRESH_KEY)).toBeNull()
    expect(localStorage.getItem(ROLE_KEY)).toBeNull()
  })

  it('refuses to be used outside its provider', () => {
    // React logs render errors to console.error; silence just this expected one.
    const quiet = vi.spyOn(console, 'error').mockImplementation(() => {})
    try {
      expect(() => render(<Probe />)).toThrow(/within AuthProvider/)
    } finally {
      quiet.mockRestore()
    }
  })
})
