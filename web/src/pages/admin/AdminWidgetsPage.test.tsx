import { describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AdminWidgetsPage } from './AdminWidgetsPage'
import { renderWithProviders, signIn, stubFetch } from '../../test/render'

/**
 * Catalog administration. The delete path is the one that can destroy data, so what matters is
 * that nothing is sent before the confirmation, that cancelling sends nothing at all, and that a
 * Manager is never shown the control in the first place.
 */
describe('AdminWidgetsPage', () => {
  const widget = {
    id: 'w-1',
    sku: 'WW-001',
    name: 'Standard Widget',
    description: 'A dependable widget.',
    imageUrl: null,
    price: 12.5,
    quantityOnHand: 10,
    quantityReserved: 0,
    quantityAvailable: 10,
    isActive: true,
  }

  const catalog = () => ({ items: [widget], page: 1, pageSize: 100, total: 1 })

  it('lists the catalog it loads', async () => {
    signIn('Administrator')
    stubFetch([['/admin/catalog/widgets', catalog]])

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })

    expect(await screen.findByText('Standard Widget')).toBeInTheDocument()
    expect(screen.getByText('WW-001')).toBeInTheDocument()
  })

  it('asks before deleting, and sends nothing until confirmed', async () => {
    signIn('Administrator')
    const calls = stubFetch([['/admin/catalog/widgets', catalog]])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))

    // The dialog names the widget and explains the two outcomes before anything happens.
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/Delete this widget\?/)).toBeInTheDocument()
    expect(within(dialog).getByText(/archived/)).toBeInTheDocument()
    expect(calls.some((c) => c.init?.method === 'DELETE')).toBe(false)
  })

  it('cancelling closes the dialog without touching the API', async () => {
    signIn('Administrator')
    const calls = stubFetch([['/admin/catalog/widgets', catalog]])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Cancel' }))

    await waitFor(() => expect(screen.queryByText('Delete this widget?')).not.toBeVisible())
    expect(calls.some((c) => c.init?.method === 'DELETE')).toBe(false)
    expect(screen.getByText('Standard Widget')).toBeInTheDocument()
  })

  it('confirming deletes and reports that it was removed outright', async () => {
    signIn('Administrator')
    const calls = stubFetch([
      ['/admin/catalog/widgets/w-1', () => ({ outcome: 'Deleted' })],
      ['/admin/catalog/widgets', catalog],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Delete widget' }))

    await waitFor(() => expect(screen.getByText(/was deleted/)).toBeInTheDocument())
    expect(screen.getByText(/no order history/)).toBeInTheDocument()

    const del = calls.find((c) => c.init?.method === 'DELETE')
    expect(del?.url).toContain('/admin/catalog/widgets/w-1')
  })

  it('reports an archive differently from a delete, because the outcome differs', async () => {
    signIn('Administrator')
    stubFetch([
      ['/admin/catalog/widgets/w-1', () => ({ outcome: 'Archived', orderLineCount: 3 })],
      ['/admin/catalog/widgets', catalog],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Delete widget' }))

    // Staff need to know the widget still exists behind past orders, and on how many.
    await waitFor(() => expect(screen.getByText(/archived instead/)).toBeInTheDocument())
    expect(screen.getByText(/3 order lines/)).toBeInTheDocument()
  })

  it('does not offer delete to a manager', async () => {
    signIn('Manager')
    stubFetch([['/admin/catalog/widgets', catalog]])

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })

    expect(await screen.findByText('Standard Widget')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Standard Widget' })).not.toBeInTheDocument()
  })

  it('shows the API error instead of failing silently', async () => {
    signIn('Administrator')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ error: 'Catalog unavailable.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    )))

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })

    expect(await screen.findByText(/Catalog unavailable/)).toBeInTheDocument()
  })

  it('adjusting stock posts the delta', async () => {
    signIn('Administrator')
    const calls = stubFetch([
      ['/inventory', () => ({ ...widget, quantityOnHand: 20, quantityAvailable: 20 })],
      ['/admin/catalog/widgets', catalog],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Add 10 to Standard Widget' }))

    await waitFor(() => expect(calls.some((c) => c.url.includes('/inventory'))).toBe(true))
    const call = calls.find((c) => c.url.includes('/inventory'))
    expect(JSON.parse(String(call?.init?.body))).toMatchObject({ quantityOnHandDelta: 10 })
  })
})
