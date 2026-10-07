import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AccountPage } from './AccountPage'
import { AccountSecurityPage } from './AccountSecurityPage'
import { TrackOrderPage } from './TrackOrderPage'
import { AccountMenu } from '../components/AccountMenu'
import { renderWithProviders, signIn, stubFetch } from '../test/render'

const profile = {
  email: 'jane@example.com',
  displayName: 'Jane',
  role: 'Customer',
  twoFactorEnabled: false,
  hasPassword: true,
  memberSince: '2024-03-01T00:00:00Z',
}

const order = {
  id: 'o-1',
  orderNumber: 'WW-20260501-ABC123',
  status: 'Paid',
  total: 29.19,
  itemCount: 2,
  createdAt: '2026-05-01T08:00:00Z',
  email: 'jane@example.com',
  paymentUnconfirmedAt: null,
}

/**
 * The account area.
 *
 * Shaped after what Company A and Company B actually do rather than from memory — the header slot keeps
 * its identity across signed-out and signed-in, "create an account" is small print rather than a
 * sibling button, and the landing page leads with orders instead of a grid of links.
 */
describe('AccountMenu', () => {
  it('leads with sign in and demotes create account to small print', async () => {
    const user = userEvent.setup()
    stubFetch([['/account/profile', () => profile]])

    renderWithProviders(<AccountMenu />, { at: '/store' })
    await user.click(screen.getByRole('button', { name: /Hello, sign in/ }))

    const menu = screen.getByRole('menu')

    // A button for signing in; a link, in a sentence, for creating one. Offering them as equals is
    // the thing none of the three big stores do.
    expect(within(menu).getByRole('link', { name: 'Sign in' })).toHaveClass('btn-primary')
    expect(within(menu).getByText(/New to WidgetWorks/)).toBeInTheDocument()

    // Guest checkout exists, so guest order tracking has to be reachable without an account.
    expect(within(menu).getByRole('menuitem', { name: 'Track an order' })).toBeInTheDocument()
  })

  it('keeps the same slot when signed in, greeting by name', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => profile]])
    const user = userEvent.setup()

    renderWithProviders(<AccountMenu />, { at: '/store' })

    // The greeting replaces "Hello, sign in" in place — the slot does not become a different control.
    expect(await screen.findByRole('button', { name: /Hello, Jane/ })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /Hello, Jane/ }))
    const menu = screen.getByRole('menu')

    // Orders first: the job outranks the settings.
    const items = within(menu).getAllByRole('menuitem').map((i) => i.textContent)
    expect(items).toEqual(['Your orders', 'Account home', 'Login and security', 'Sign out'])
  })

  it('falls back to a neutral greeting when no name is set', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => ({ ...profile, displayName: null })]])

    renderWithProviders(<AccountMenu />, { at: '/store' })

    // "Hello, " with nothing after it would be worse than not greeting at all.
    expect(await screen.findByRole('button', { name: /Hello, there/ })).toBeInTheDocument()
  })

  it('still renders when the profile cannot be loaded', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 500 })))

    renderWithProviders(<AccountMenu />, { at: '/store' })

    // A missing name is not worth an error banner in the header.
    expect(await screen.findByRole('button', { name: /Hello, there/ })).toBeInTheDocument()
  })

  it('closes on Escape and on a click outside', async () => {
    const user = userEvent.setup()
    stubFetch([['/account/profile', () => profile]])

    renderWithProviders(<AccountMenu />, { at: '/store' })
    const trigger = screen.getByRole('button', { name: /Hello, sign in/ })

    await user.click(trigger)
    expect(screen.getByRole('menu')).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()

    // Clicking elsewhere closes it too. A dropdown that only closes via its own trigger is the
    // thing that makes a hand-rolled menu feel broken.
    await user.click(trigger)
    expect(screen.getByRole('menu')).toBeInTheDocument()
    await user.click(document.body)
    await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
  })

  // Roles differ on purpose: the two account routes are menu items, while Sign in and Create an
  // account are a button-styled link and a link in a sentence — they are not menu choices, they are
  // the two ways in.
  it.each([
    ['Track an order', 'menuitem', '/track-order', 'Track'],
    ['Create an account', 'link', '/register', 'Register'],
    ['Sign in', 'link', '/login', 'Sign in page'],
  ])('goes to %s from the signed-out menu', async (name, role, path, heading) => {
    const user = userEvent.setup()
    stubFetch([['/account/profile', () => profile]])

    renderWithProviders(<AccountMenu />, { at: '/store', routes: { [path]: <h1>{heading}</h1> } })

    await user.click(screen.getByRole('button', { name: /Hello, sign in/ }))
    await user.click(within(screen.getByRole('menu')).getByRole(role, { name }))

    // Navigating unmounts the menu with the page, which is the dismissal — there is no stale
    // dropdown floating over the destination.
    expect(await screen.findByRole('heading', { name: heading })).toBeInTheDocument()
  })

  it.each([
    ['Your orders', '/orders', 'Orders'],
    ['Account home', '/account', 'Account'],
    ['Login and security', '/account/security', 'Security'],
  ])('goes to %s from the signed-in menu', async (name, path, heading) => {
    signIn('Customer')
    stubFetch([['/account/profile', () => profile]])
    const user = userEvent.setup()

    renderWithProviders(<AccountMenu />, { at: '/store', routes: { [path]: <h1>{heading}</h1> } })

    await user.click(await screen.findByRole('button', { name: /Hello, / }))
    await user.click(screen.getByRole('menuitem', { name }))

    expect(await screen.findByRole('heading', { name: heading })).toBeInTheDocument()
  })

  it('signs out and leaves no session behind', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => profile]])
    const user = userEvent.setup()

    renderWithProviders(<AccountMenu />, { at: '/store', routes: { '/store': <h1>Storefront</h1> } })
    await user.click(await screen.findByRole('button', { name: /Hello, Jane/ }))
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))

    expect(await screen.findByRole('button', { name: /Hello, sign in/ })).toBeInTheDocument()
  })
})

describe('AccountPage', () => {
  it('lands on recent orders rather than a grid of links', async () => {
    signIn('Customer')
    stubFetch([
      ['/account/profile', () => profile],
      ['/orders', () => [order]],
    ])

    renderWithProviders(<AccountPage />, { at: '/account' })

    // Company B's answer: the landing page shows what you came for. Finding an order is
    // overwhelmingly why anyone opens an account page.
    expect(await screen.findByText('WW-20260501-ABC123')).toBeInTheDocument()
    expect(screen.getByText('Paid')).toBeInTheDocument()
    expect(screen.getByText(/Member since 2024/)).toBeInTheDocument()
  })

  it('invites a first order rather than showing an empty table', async () => {
    signIn('Customer')
    stubFetch([
      ['/account/profile', () => profile],
      ['/orders', () => []],
    ])

    renderWithProviders(<AccountPage />, { at: '/account' })

    expect(await screen.findByText('No orders yet')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Start shopping' })).toBeInTheDocument()
  })

  it('offers all orders only when there are more than it shows', async () => {
    signIn('Customer')
    const many = Array.from({ length: 5 }, (_, i) => ({ ...order, id: `o-${i}`, orderNumber: `WW-${i}` }))
    stubFetch([
      ['/account/profile', () => profile],
      ['/orders', () => many],
    ])

    renderWithProviders(<AccountPage />, { at: '/account' })

    expect(await screen.findByRole('link', { name: 'View all orders' })).toBeInTheDocument()

    // Three shown, not five — the panel is a summary, not the orders page.
    expect(screen.getAllByRole('link', { name: 'View order' })).toHaveLength(3)
  })

  it('reports a load failure instead of an endless skeleton', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/orders')) {
        return new Response(JSON.stringify({ error: 'Orders are unavailable.' }), {
          status: 500, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(profile), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))

    renderWithProviders(<AccountPage />, { at: '/account' })

    expect(await screen.findByText('Orders are unavailable.')).toBeInTheDocument()
  })
})

describe('AccountSecurityPage', () => {
  it('keeps the name here rather than on a profile page of its own', async () => {
    signIn('Customer')
    const calls = stubFetch([['/account/profile', () => profile]])
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    const field = await screen.findByLabelText('Name')
    await user.clear(field)
    await user.type(field, 'Janet')
    await user.click(screen.getByRole('button', { name: 'Save name' }))

    await waitFor(() => {
      const put = calls.find((c) => c.init?.method === 'PUT')
      expect(JSON.parse(String(put?.init?.body))).toEqual({ displayName: 'Janet' })
    })
    expect(await screen.findByText('Name saved.')).toBeInTheDocument()
  })

  it('says why the email cannot be changed instead of showing a dead control', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => profile]])

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    expect(await screen.findByText('jane@example.com')).toBeInTheDocument()
    expect(screen.getByText(/confirmation sent to\s+the new address/)).toBeInTheDocument()
  })

  it('will not submit a password change until both fields are usable', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => profile]])
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    const submit = await screen.findByRole('button', { name: 'Change password' })
    expect(submit).toBeDisabled()

    await user.type(screen.getByLabelText('Current password'), 'Str0ng!Passw0rd')
    await user.type(screen.getByLabelText('New password'), 'weak')
    expect(submit).toBeDisabled()

    await user.clear(screen.getByLabelText('New password'))
    await user.type(screen.getByLabelText('New password'), 'An0ther!Passw0rd')
    expect(submit).toBeEnabled()
  })

  it('swaps in the new tokens so the change does not look like a sign-out', async () => {
    signIn('Customer')
    const calls = stubFetch([
      ['/account/profile', () => profile],
      ['/account/password', () => ({
        accessToken: 'new-access',
        accessTokenExpiresAt: '2026-01-01T00:00:00Z',
        refreshToken: 'new-refresh',
        refreshTokenExpiresAt: '2026-02-01T00:00:00Z',
        role: 'Customer',
      })],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    await user.type(await screen.findByLabelText('Current password'), 'Str0ng!Passw0rd')
    await user.type(screen.getByLabelText('New password'), 'An0ther!Passw0rd')
    await user.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText(/Password changed/)).toBeInTheDocument()
    expect(calls.some((c) => c.url.includes('/account/password'))).toBe(true)

    // The server killed every session including this one, so the fresh pair has to be stored or the
    // next request 401s on a page that just reported success.
    expect(localStorage.getItem('ww.refreshToken')).toBe('new-refresh')
  })

  it('shows the server reason when the current password is wrong', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/account/password') && init?.method === 'POST') {
        return new Response(JSON.stringify({ error: 'That is not your current password.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(profile), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    await user.type(await screen.findByLabelText('Current password'), 'Wr0ng!Passw0rd')
    await user.type(screen.getByLabelText('New password'), 'An0ther!Passw0rd')
    await user.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText('That is not your current password.')).toBeInTheDocument()
  })

  it('offers no password section to a Google-only account', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => ({ ...profile, hasPassword: false })]])

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    await screen.findByText('jane@example.com')

    // There is no password to change, and a disabled button with no explanation is worse than none.
    expect(screen.queryByRole('button', { name: 'Change password' })).not.toBeInTheDocument()
  })

  it('warns about recovery codes when two-step is on', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => ({ ...profile, twoFactorEnabled: true })]])

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    expect(await screen.findByText(/On — authenticator app/)).toBeInTheDocument()

    // Losing both the app and the codes needs staff to intervene; saying so is the honest version.
    expect(screen.getByText(/staff have to reset it for you/)).toBeInTheDocument()
  })

  it('signs every device out on request', async () => {
    signIn('Customer')
    const calls = stubFetch([
      ['/account/profile', () => profile],
      ['/auth/secure-account', () => ({})],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })
    await user.click(await screen.findByRole('button', { name: 'Sign out all devices' }))

    expect(await screen.findByText(/Every other session has been ended/)).toBeInTheDocument()
    expect(calls.some((c) => c.url.includes('/auth/secure-account'))).toBe(true)
  })

  it('reports failures from the name and sign-out actions', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/account/profile') && init?.method === 'PUT') throw 'network exploded'
      if (url.includes('/auth/secure-account')) throw 'network exploded'
      return new Response(JSON.stringify(profile), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    await user.click(await screen.findByRole('button', { name: 'Save name' }))
    expect(await screen.findByText('Could not save your name.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Sign out all devices' }))
    expect(await screen.findByText('Could not sign out your other sessions.')).toBeInTheDocument()
  })

  it('reports a profile that will not load', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Account unavailable.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    expect(await screen.findByText('Account unavailable.')).toBeInTheDocument()
  })
})

describe('TrackOrderPage', () => {
  it('finds a guest order from the number and email', async () => {
    const calls = stubFetch([['/orders/lookup', () => ({
      ...order,
      items: [{ widgetId: 'w-1', sku: 'WW-001', name: 'Gizmo', unitPrice: 10, quantity: 2, lineSubtotal: 20 }],
      trackingNumber: 'TRK-9',
    })]])
    const user = userEvent.setup()

    renderWithProviders(<TrackOrderPage />, { at: '/track-order' })

    await user.type(screen.getByLabelText('Order number'), 'WW-20260501-ABC123')
    await user.type(screen.getByLabelText('Email address'), 'jane@example.com')
    await user.click(screen.getByRole('button', { name: 'Find my order' }))

    expect(await screen.findByText('WW-20260501-ABC123')).toBeInTheDocument()
    expect(screen.getByText('TRK-9')).toBeInTheDocument()

    // Both values go to the server: an order number alone would let anyone walk the sequence.
    const sent = calls.find((c) => c.url.includes('/orders/lookup'))!.url
    expect(sent).toContain('number=WW-20260501-ABC123')
    expect(sent).toContain('email=jane%40example.com')
  })

  it('shows the server refusal when nothing matches', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Order not found.' }),
      { status: 404, headers: { 'Content-Type': 'application/json' } },
    )))
    const user = userEvent.setup()

    renderWithProviders(<TrackOrderPage />, { at: '/track-order' })

    await user.type(screen.getByLabelText('Order number'), 'WW-NOPE')
    await user.type(screen.getByLabelText('Email address'), 'nobody@example.com')
    await user.click(screen.getByRole('button', { name: 'Find my order' }))

    expect(await screen.findByText('Order not found.')).toBeInTheDocument()
  })

  it('falls back to its own message when the failure is not an Error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw 'network exploded' }))
    const user = userEvent.setup()

    renderWithProviders(<TrackOrderPage />, { at: '/track-order' })

    await user.type(screen.getByLabelText('Order number'), 'WW-1')
    await user.type(screen.getByLabelText('Email address'), 'jane@example.com')
    await user.click(screen.getByRole('button', { name: 'Find my order' }))

    expect(await screen.findByText('Could not find that order.')).toBeInTheDocument()
  })

  it('omits tracking from an order that has none', async () => {
    stubFetch([['/orders/lookup', () => ({ ...order, items: [], trackingNumber: null })]])
    const user = userEvent.setup()

    renderWithProviders(<TrackOrderPage />, { at: '/track-order' })

    await user.type(screen.getByLabelText('Order number'), 'WW-1')
    await user.type(screen.getByLabelText('Email address'), 'jane@example.com')
    await user.click(screen.getByRole('button', { name: 'Find my order' }))

    await screen.findByText('WW-20260501-ABC123')
    expect(screen.queryByText('Tracking')).not.toBeInTheDocument()
  })
})

describe('account edges', () => {
  it('ignores keys that are not Escape', async () => {
    const user = userEvent.setup()
    stubFetch([['/account/profile', () => profile]])

    renderWithProviders(<AccountMenu />, { at: '/store' })
    await user.click(screen.getByRole('button', { name: /Hello, sign in/ }))

    // Only Escape closes it. A menu that vanished on any keypress would be unusable with a keyboard.
    await user.keyboard('a')
    expect(screen.getByRole('menu')).toBeInTheDocument()
  })

  it('drops a profile that arrives after the menu has gone', async () => {
    signIn('Customer')
    // Initialised to a no-op rather than null: TypeScript cannot see that the Promise executor runs
    // synchronously, so a nullable here narrows to null at the call below.
    let release: () => void = () => {}
    vi.stubGlobal('fetch', vi.fn(() => new Promise((resolve) => {
      release = () => resolve(new Response(JSON.stringify(profile), {
        status: 200, headers: { 'Content-Type': 'application/json' },
      }))
    })))

    const { unmount } = renderWithProviders(<AccountMenu />, { at: '/store' })
    unmount()

    // Resolving after unmount must not set state on a dead component — that is the React warning
    // everyone learns to ignore, and ignoring it is how real leaks hide.
    release()
    await waitFor(() => expect(true).toBe(true))
  })

  it('counts a single item in the singular', async () => {
    signIn('Customer')
    stubFetch([
      ['/account/profile', () => profile],
      ['/orders', () => [{ ...order, itemCount: 1 }]],
    ])

    renderWithProviders(<AccountPage />, { at: '/account' })

    expect(await screen.findByText('1 item')).toBeInTheDocument()
  })

  it('starts the name box empty when no name is set', async () => {
    signIn('Customer')
    stubFetch([['/account/profile', () => ({ ...profile, displayName: null })]])

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    expect(await screen.findByLabelText('Name')).toHaveValue('')
  })

  it('falls back to its own messages when failures are not Errors', async () => {
    signIn('Customer')

    // The profile load itself rejects with a non-Error.
    vi.stubGlobal('fetch', vi.fn(async () => { throw 'network exploded' }))
    const { unmount } = renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })
    expect(await screen.findByText('Could not load your account.')).toBeInTheDocument()
    unmount()

    // And so does the password change.
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/account/password') && init?.method === 'POST') throw 'network exploded'
      return new Response(JSON.stringify(profile), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()
    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    await user.type(await screen.findByLabelText('Current password'), 'Str0ng!Passw0rd')
    await user.type(screen.getByLabelText('New password'), 'An0ther!Passw0rd')
    await user.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText('Could not change your password.')).toBeInTheDocument()
  })

  it('shows the server reason when saving a name or signing out is refused', async () => {
    signIn('Customer')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const refuse = (error: string) => new Response(JSON.stringify({ error }), {
        status: 400, headers: { 'Content-Type': 'application/json' },
      })
      if (url.includes('/account/profile') && init?.method === 'PUT') return refuse('Name must be 60 characters or fewer.')
      if (url.includes('/auth/secure-account')) return refuse('Try again in a minute.')
      return new Response(JSON.stringify(profile), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AccountSecurityPage />, { at: '/account/security' })

    // The server's wording, not ours — it knows why, and a generic message would hide the limit.
    await user.click(await screen.findByRole('button', { name: 'Save name' }))
    expect(await screen.findByText('Name must be 60 characters or fewer.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Sign out all devices' }))
    expect(await screen.findByText('Try again in a minute.')).toBeInTheDocument()
  })
})
