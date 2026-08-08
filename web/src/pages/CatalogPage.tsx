import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { Paged, WidgetView } from '../api/types'
import { money } from '../lib/format'
import { useCart } from '../cart/CartContext'

export function CatalogPage() {
  const [search, setSearch] = useState('')
  const [data, setData] = useState<Paged<WidgetView> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const { addItem } = useCart()

  useEffect(() => {
    let active = true
    setLoading(true)
    const q = search.trim() ? `?search=${encodeURIComponent(search.trim())}` : ''
    api<Paged<WidgetView>>(`/catalog/widgets${q}`)
      .then((d) => { if (active) { setData(d); setError(null) } })
      .catch((e) => { if (active) setError(e.message) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [search])

  return (
    <section>
      <h1>Widgets</h1>
      <input
        className="search"
        placeholder="Search widgets…"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />
      {loading && <p>Loading…</p>}
      {error && <p className="error">{error}</p>}
      <div className="grid">
        {data?.items.map((w) => (
          <div key={w.id} className="card">
            <Link to={`/widgets/${w.id}`} className="card-title">{w.name}</Link>
            <p className="muted">{w.description}</p>
            <div className="price">{money(w.price)}</div>
            <div className="muted small">{w.quantityAvailable > 0 ? `${w.quantityAvailable} in stock` : 'Out of stock'}</div>
            <button disabled={w.quantityAvailable <= 0} onClick={() => addItem(w.id, 1)}>Add to cart</button>
          </div>
        ))}
      </div>
      {data && data.items.length === 0 && <p className="muted">No widgets found.</p>}
    </section>
  )
}
