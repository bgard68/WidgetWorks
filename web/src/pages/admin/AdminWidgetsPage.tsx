import { useCallback, useEffect, useState } from 'react'
import { api } from '../../api/client'
import type { Paged, WidgetView } from '../../api/types'
import { money } from '../../lib/format'

const empty = { sku: '', name: '', description: '', price: '0', quantityOnHand: '0' }

export function AdminWidgetsPage() {
  const [items, setItems] = useState<WidgetView[]>([])
  const [error, setError] = useState<string | null>(null)
  const [form, setForm] = useState(empty)

  const load = useCallback(() => {
    api<Paged<WidgetView>>('/admin/catalog/widgets?pageSize=100')
      .then((d) => setItems(d.items))
      .catch((e) => setError(e.message))
  }, [])

  useEffect(() => { load() }, [load])

  async function create(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
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
    }
  }

  async function adjust(id: string, delta: number) {
    await api(`/admin/catalog/widgets/${id}/inventory`, { method: 'POST', body: { quantityOnHandDelta: delta } })
    load()
  }

  async function toggleActive(w: WidgetView) {
    await api(`/admin/catalog/widgets/${w.id}`, {
      method: 'PUT',
      body: { name: w.name, description: w.description, imageUrl: w.imageUrl, price: w.price, isActive: !w.isActive },
    })
    load()
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value })

  return (
    <section>
      <h1>Admin · Catalog</h1>
      {error && <p className="error">{error}</p>}
      <form onSubmit={create} className="form inline">
        <input placeholder="SKU" value={form.sku} onChange={set('sku')} required />
        <input placeholder="Name" value={form.name} onChange={set('name')} required />
        <input placeholder="Description" value={form.description} onChange={set('description')} />
        <input placeholder="Price" type="number" step="0.01" value={form.price} onChange={set('price')} />
        <input placeholder="Qty" type="number" value={form.quantityOnHand} onChange={set('quantityOnHand')} />
        <button>Add widget</button>
      </form>
      <table className="table">
        <thead><tr><th>SKU</th><th>Name</th><th>Price</th><th>Avail</th><th>Active</th><th>Stock</th></tr></thead>
        <tbody>
          {items.map((w) => (
            <tr key={w.id}>
              <td>{w.sku}</td>
              <td>{w.name}</td>
              <td>{money(w.price)}</td>
              <td>{w.quantityAvailable}</td>
              <td><button className="linkbtn" onClick={() => toggleActive(w)}>{w.isActive ? 'Active' : 'Hidden'}</button></td>
              <td>
                <button className="linkbtn" onClick={() => adjust(w.id, 10)}>+10</button>{' '}
                <button className="linkbtn" onClick={() => adjust(w.id, -10)}>−10</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
