import { useCallback, useEffect, useState } from 'react'
import { api } from '../../api/client'
import type { Paged, WidgetView } from '../../api/types'
import { money } from '../../lib/format'
import { useAuth } from '../../auth/AuthContext'
import { ProductImage } from '../../components/ProductImage'
import { ConfirmDialog } from '../../components/ConfirmDialog'

const empty = { sku: '', name: '', description: '', price: '0', quantityOnHand: '0' }

interface DeleteResponse {
  outcome: 'Deleted' | 'Archived'
  orderLineCount: number
}

export function AdminWidgetsPage() {
  const { isAdmin } = useAuth()
  const [items, setItems] = useState<WidgetView[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [form, setForm] = useState(empty)
  const [busy, setBusy] = useState(false)

  // The widget the confirm dialog is about — also what the dialog shows a thumbnail of.
  const [target, setTarget] = useState<WidgetView | null>(null)
  const [deleting, setDeleting] = useState(false)

  const load = useCallback(() => {
    api<Paged<WidgetView>>('/admin/catalog/widgets?pageSize=100')
      .then((d) => setItems(d.items))
      .catch((e) => setError(e.message))
  }, [])

  useEffect(() => { load() }, [load])

  async function create(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      await api('/admin/catalog/widgets', {
        method: 'POST',
        body: {
          sku: form.sku,
          name: form.name,
          description: form.description,
          imageUrl: null,
          price: Number(form.price),
          quantityOnHand: Number(form.quantityOnHand),
        },
      })
      setForm(empty)
      load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Create failed.')
    } finally {
      setBusy(false)
    }
  }

  async function adjust(id: string, delta: number) {
    setError(null)
    try {
      await api(`/admin/catalog/widgets/${id}/inventory`, { method: 'POST', body: { quantityOnHandDelta: delta } })
      load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Stock adjustment failed.')
    }
  }

  async function toggleActive(w: WidgetView) {
    setError(null)
    try {
      await api(`/admin/catalog/widgets/${w.id}`, {
        method: 'PUT',
        body: { name: w.name, description: w.description, imageUrl: w.imageUrl, price: w.price, isActive: !w.isActive },
      })
      load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Update failed.')
    }
  }

  // A widget with order history can't be removed outright — order_items still
  // references it — so the API archives it instead and tells us which happened.
  async function confirmDelete() {
    /* v8 ignore next -- the dialog is open={target !== null}, so confirming implies a target */
    if (!target) return
    setDeleting(true)
    setError(null)
    try {
      const res = await api<DeleteResponse>(`/admin/catalog/widgets/${target.id}`, { method: 'DELETE' })
      setNotice(
        res.outcome === 'Deleted'
          ? `“${target.name}” was deleted. It had no order history.`
          : `“${target.name}” appears on ${res.orderLineCount} order ${
            res.orderLineCount === 1 ? 'line' : 'lines'
          }, so it was archived instead — pulled from the store, but kept so those orders stay reportable.`,
      )
      setTarget(null)
      load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed.')
      setTarget(null)
    } finally {
      setDeleting(false)
    }
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm({ ...form, [k]: e.target.value })

  return (
    <>
      <div className="pagehead">
        <div>
          <div className="admin-head">
            <span className="admin-tag">Admin</span>
            <h1>Catalog</h1>
          </div>
          <p>{items.length} widgets. Adjust stock in tens, hide a product from the storefront{isAdmin ? ', or remove it entirely' : ''}.</p>
        </div>
      </div>

      {error && <p className="alert alert-err" style={{ marginBottom: 14 }}>{error}</p>}
      {notice && (
        <p className="alert alert-ok" style={{ marginBottom: 14 }}>
          {notice}
          <button type="button" className="btn-link" style={{ marginLeft: 'auto' }} onClick={() => setNotice(null)}>
            Dismiss
          </button>
        </p>
      )}

      <div className="panel" style={{ marginBottom: 18 }}>
        <div className="panel-head"><h2>Add a widget</h2></div>
        <div className="panel-body">
          <form onSubmit={create} className="admin-form">
            <label className="field">
              <span>SKU</span>
              <input placeholder="WW-006" value={form.sku} onChange={set('sku')} required />
            </label>
            <label className="field wide">
              <span>Name</span>
              <input placeholder="Turbo Widget" value={form.name} onChange={set('name')} required />
            </label>
            <label className="field wide">
              <span>Description</span>
              <input placeholder="Short shelf description" value={form.description} onChange={set('description')} />
            </label>
            <label className="field">
              <span>Price</span>
              <input type="number" step="0.01" min="0" value={form.price} onChange={set('price')} />
            </label>
            <label className="field">
              <span>Qty on hand</span>
              <input type="number" min="0" value={form.quantityOnHand} onChange={set('quantityOnHand')} />
            </label>
            <button className="btn btn-solid" disabled={busy}>{busy ? 'Adding…' : 'Add widget'}</button>
          </form>
        </div>
      </div>

      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th><span className="sr-only">Image</span></th>
              <th>SKU</th><th>Name</th><th className="num">Price</th>
              <th className="num">Available</th><th>Visibility</th><th>Adjust stock</th>
              {isAdmin && <th>Remove</th>}
            </tr>
          </thead>
          <tbody>
            {items.map((w) => (
              <tr key={w.id}>
                <td>
                  <span className="tile-img sm">
                    <ProductImage sku={w.sku} imageUrl={w.imageUrl} alt={w.name} />
                  </span>
                </td>
                <td className="nums">{w.sku}</td>
                <td className="strong">{w.name}</td>
                <td className="num nums">{money(w.price)}</td>
                <td className="num nums">{w.quantityAvailable}</td>
                <td>
                  <button
                    className={`pill ${w.isActive ? 'pill-ok' : 'pill-err'}`}
                    style={{ cursor: 'pointer' }}
                    onClick={() => toggleActive(w)}
                    title="Toggle storefront visibility"
                  >
                    {w.isActive ? 'Live' : 'Hidden'}
                  </button>
                </td>
                <td>
                  <span className="step-mini">
                    <button type="button" onClick={() => adjust(w.id, 10)} aria-label={`Add 10 to ${w.name}`}>+10</button>
                    <button type="button" onClick={() => adjust(w.id, -10)} aria-label={`Remove 10 from ${w.name}`}>−10</button>
                  </span>
                </td>
                {isAdmin && (
                  <td>
                    <button
                      type="button"
                      className="btn btn-danger btn-sm"
                      onClick={() => { setNotice(null); setTarget(w) }}
                      aria-label={`Delete ${w.name}`}
                    >
                      Delete
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <ConfirmDialog
        open={target !== null}
        title="Delete this widget?"
        confirmLabel="Delete widget"
        danger
        busy={deleting}
        onConfirm={confirmDelete}
        onCancel={() => setTarget(null)}
      >
        {target && (
          <div className="subject">
            <span className="tile-img md">
              <ProductImage sku={target.sku} imageUrl={target.imageUrl} alt={target.name} />
            </span>
            <span>
              <span className="nm">{target.name}</span>
              <span className="sku" style={{ display: 'block' }}>
                SKU {target.sku} · {money(target.price)} · {target.quantityAvailable} available
              </span>
            </span>
          </div>
        )}
        <p>
          If this widget has never been ordered it is removed permanently. If it appears on
          any order it is <strong>archived</strong> instead — pulled from the storefront and
          this list, but kept so those orders stay reportable.
        </p>
        <p className="help">Either way it stops being sellable. This cannot be undone from here.</p>
      </ConfirmDialog>
    </>
  )
}
