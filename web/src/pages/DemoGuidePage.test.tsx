import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { DemoGuidePage } from './DemoGuidePage'
import { renderWithProviders } from '../test/render'

/**
 * The page everyone lands on. Its job is not decorative: it has to state plainly that no money
 * changes hands, hand over working credentials for all three roles, and say what each role can do.
 * If the reassurance or a credential silently disappears, the demo becomes untrustworthy — so
 * they are asserted rather than eyeballed.
 */
describe('DemoGuidePage', () => {
  const render = () => renderWithProviders(<DemoGuidePage />, {
    at: '/',
    routes: { '/store': <h1>Storefront</h1> },
  })

  it('states up front that no payment is ever taken', () => {
    render()

    expect(screen.getByText(/No payment is ever taken/i)).toBeInTheDocument()
    expect(screen.getByText(/mock payment gateway/i)).toBeInTheDocument()
    expect(screen.getByText(/test mode/i)).toBeInTheDocument()
  })

  it('publishes all three demo accounts', () => {
    render()

    for (const email of ['demo@widgetworks.demo', 'manager@widgetworks.demo', 'admin@widgetworks.demo']) {
      expect(screen.getByText(email)).toBeInTheDocument()
    }

    expect(screen.getByRole('heading', { name: 'Customer' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Manager' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Administrator' })).toBeInTheDocument()
  })

  it('explains what separates the roles, not just that they exist', () => {
    render()

    // The distinction a reviewer is most likely to probe.
    expect(screen.getByText(/Delete or retire a widget/i)).toBeInTheDocument()
    expect(screen.getByText(/Manage users/i)).toBeInTheDocument()
  })

  it('copies a credential to the clipboard and confirms it did', async () => {
    // user-event installs its own clipboard, so this goes through the real navigator API.
    const user = userEvent.setup()
    render()

    await user.click(screen.getAllByRole('button', { name: 'Copy' })[0])

    expect(await navigator.clipboard.readText()).toBe('demo@widgetworks.demo')
    await waitFor(() => expect(screen.getByRole('button', { name: '✓ Copied' })).toBeInTheDocument())
  })

  it('survives a browser that refuses clipboard access', async () => {
    const user = userEvent.setup()
    render()
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'))

    await user.click(screen.getAllByRole('button', { name: 'Copy' })[0])

    // No crash, and the value stays on screen so it can be typed instead.
    expect(screen.getByText('demo@widgetworks.demo')).toBeInTheDocument()
  })

  it('points at the store and at the order history for receipts', () => {
    render()

    expect(screen.getAllByRole('link', { name: /Enter the store/i }).length).toBeGreaterThan(0)
    expect(screen.getByRole('link', { name: /Your orders/i })).toBeInTheDocument()
  })

  it('explains where the email goes instead of an inbox', () => {
    render()

    expect(screen.getByText(/written to the application log/i)).toBeInTheDocument()
  })
})
