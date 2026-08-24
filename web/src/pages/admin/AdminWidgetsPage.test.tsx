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

  it('removing stock posts a negative delta', async () => {
    signIn('Administrator')
    const calls = stubFetch([
      ['/inventory', () => ({ ...widget, quantityOnHand: 0, quantityAvailable: 0 })],
      ['/admin/catalog/widgets', catalog],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Remove 10 from Standard Widget' }))

    await waitFor(() => expect(calls.some((c) => c.url.includes('/inventory'))).toBe(true))
    const call = calls.find((c) => c.url.includes('/inventory'))
    expect(JSON.parse(String(call?.init?.body))).toMatchObject({ quantityOnHandDelta: -10 })
  })

  it('reports a refused stock adjustment', async () => {
    signIn('Administrator')
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        return new Response(JSON.stringify({ error: 'On-hand cannot drop below the reserved quantity.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(catalog()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Remove 10 from Standard Widget' }))

    expect(await screen.findByText(/cannot drop below the reserved/)).toBeInTheDocument()
  })

  // ---- create -------------------------------------------------------------------------

  async function fillNewWidget(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('SKU'), 'WW-006')
    await user.type(screen.getByLabelText('Name'), 'Turbo Widget')
    await user.type(screen.getByLabelText('Description'), 'Fast.')
    await user.clear(screen.getByLabelText('Price'))
    await user.type(screen.getByLabelText('Price'), '19.99')
    await user.clear(screen.getByLabelText('Qty on hand'))
    await user.type(screen.getByLabelText('Qty on hand'), '25')
    await user.click(screen.getByRole('button', { name: 'Add widget' }))
  }

  it('creates a widget from the form, sending numbers as numbers', async () => {
    signIn('Administrator')
    const calls = stubFetch([['/admin/catalog/widgets', catalog]])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await screen.findByText('Standard Widget')
    await fillNewWidget(user)

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'POST')).toBe(true))
    const post = calls.find((c) => c.init?.method === 'POST')
    // Price and quantity are typed into text inputs; the API needs numbers, not strings.
    expect(JSON.parse(String(post?.init?.body))).toEqual({
      sku: 'WW-006',
      name: 'Turbo Widget',
      description: 'Fast.',
      imageUrl: null,
      price: 19.99,
      quantityOnHand: 25,
    })

    // The form empties itself, ready for the next one.
    await waitFor(() => expect(screen.getByLabelText('SKU')).toHaveValue(''))
  })

  it('keeps what was typed when the create is rejected', async () => {
    signIn('Administrator')
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        return new Response(JSON.stringify({ error: 'A widget with that SKU already exists.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(catalog()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await screen.findByText('Standard Widget')
    await fillNewWidget(user)

    expect(await screen.findByText(/SKU already exists/)).toBeInTheDocument()
    // Re-typing everything after a duplicate SKU would be its own small cruelty.
    expect(screen.getByLabelText('SKU')).toHaveValue('WW-006')
  })

  // ---- visibility ---------------------------------------------------------------------

  it('toggling visibility sends the inverted flag with the widget unchanged', async () => {
    signIn('Administrator')
    const calls = stubFetch([['/admin/catalog/widgets', catalog]])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Live' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'PUT')).toBe(true))
    const put = calls.find((c) => c.init?.method === 'PUT')
    expect(JSON.parse(String(put?.init?.body))).toEqual({
      name: widget.name,
      description: widget.description,
      imageUrl: null,
      price: widget.price,
      isActive: false,
    })
  })

  it('shows a hidden widget as hidden', async () => {
    signIn('Administrator')
    stubFetch([['/admin/catalog/widgets', () => ({ items: [{ ...widget, isActive: false }], page: 1, pageSize: 100, total: 1 })]])

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })

    expect(await screen.findByRole('button', { name: 'Hidden' })).toHaveClass('pill-err')
  })

  it('reports a failed visibility toggle', async () => {
    signIn('Administrator')
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        return new Response(JSON.stringify({ error: 'Widget is archived and can no longer be edited.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(catalog()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Live' }))

    expect(await screen.findByText(/archived and can no longer be edited/)).toBeInTheDocument()
  })

  // ---- delete failure + notice --------------------------------------------------------

  it('reports a failed delete and closes the dialog rather than leaving it stuck', async () => {
    signIn('Administrator')
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'DELETE') {
        return new Response(JSON.stringify({ error: 'Widget not found.' }), {
          status: 400, headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(JSON.stringify(catalog()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Delete widget' }))

    expect(await screen.findByText('Widget not found.')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Delete this widget?')).not.toBeVisible())
  })

  it('the outcome notice can be dismissed', async () => {
    signIn('Administrator')
    stubFetch([
      ['/admin/catalog/widgets/w-1', () => ({ outcome: 'Deleted' })],
      ['/admin/catalog/widgets', catalog],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Delete widget' }))
    await screen.findByText(/was deleted/)

    await user.click(screen.getByRole('button', { name: 'Dismiss' }))

    expect(screen.queryByText(/was deleted/)).not.toBeInTheDocument()
  })

  it('reports a single order line in the singular', async () => {
    signIn('Administrator')
    stubFetch([
      ['/admin/catalog/widgets/w-1', () => ({ outcome: 'Archived', orderLineCount: 1 })],
      ['/admin/catalog/widgets', catalog],
    ])
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Delete widget' }))

    expect(await screen.findByText(/1 order line,/)).toBeInTheDocument()
  })

  it.each([
    ['create', 'Add widget', 'Create failed.'],
    ['visibility toggle', 'Live', 'Update failed.'],
    ['stock adjustment', 'Add 10 to Standard Widget', 'Stock adjustment failed.'],
  ])('falls back to its own message when the %s fails with a non-Error', async (_what, button, message) => {
    signIn('Administrator')
    let listed = false
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method && init.method !== 'GET') throw 'network exploded'
      listed = true
      return new Response(JSON.stringify(catalog()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await screen.findByText('Standard Widget')
    if (button === 'Add widget') {
      await user.type(screen.getByLabelText('SKU'), 'WW-006')
      await user.type(screen.getByLabelText('Name'), 'Turbo Widget')
    }
    await user.click(screen.getByRole('button', { name: button }))

    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(listed).toBe(true)
  })

  it('falls back to its own message when a delete fails with a non-Error', async () => {
    signIn('Administrator')
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'DELETE') throw 'network exploded'
      return new Response(JSON.stringify(catalog()), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))
    const user = userEvent.setup()

    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    await user.click(await screen.findByRole('button', { name: 'Delete Standard Widget' }))
    await user.click(await screen.findByRole('button', { name: 'Delete widget' }))

    expect(await screen.findByText('Delete failed.')).toBeInTheDocument()
  })

  it('tells an administrator that removal is available, and a manager that it is not', async () => {
    signIn('Administrator')
    stubFetch([['/admin/catalog/widgets', catalog]])
    const { unmount } = renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    expect(await screen.findByText(/or remove it entirely/)).toBeInTheDocument()
    unmount()

    localStorage.clear()
    signIn('Manager')
    stubFetch([['/admin/catalog/widgets', catalog]])
    renderWithProviders(<AdminWidgetsPage />, { at: '/admin/widgets' })
    expect(await screen.findByText(/hide a product from the storefront\./)).toBeInTheDocument()
  })
})
