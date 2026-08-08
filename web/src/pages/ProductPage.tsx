import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { api } from '../api/client'
import type { WidgetView } from '../api/types'
import { money } from '../lib/format'
import { useCart } from '../cart/CartContext'

export function ProductPage() {
  const { id } = useParams<{ id: string }>()
  const [widget, setWidget] = useState<WidgetView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [qty, setQty] = useState(1)
  const [added, setAdded] = useState(false)
  const { addItem } = useCart()

  useEffect(() => {
    if (!id) return
    api<WidgetView>(`/catalog/widgets/${id}`)
      .then(setWidget)
      .catch((e) => setError(e.message))
  }, [id])

  if (error) return <p className="error">{error}</p>
  if (!widget) return <p>Loading…</p>

  return (
    <section className="product">
      <Link to="/" className="muted">← Back to shop</Link>
      <h1>{widget.name}</h1>
      <p>{widget.description}</p>
      <div className="price">{money(widget.price)}</div>
      <div className="muted">{widget.quantityAvailable > 0 ? `${widget.quantityAvailable} in stock` : 'Out of stock'}</div>
      <div className="row">
        <input
          type="number"
          min={1}
          max={Math.max(1, widget.quantityAvailable)}
          value={qty}
          onChange={(e) => setQty(Math.max(1, Number(e.target.value)))}
        />
        <button
          disabled={widget.quantityAvailable <= 0}
          onClick={async () => { await addItem(widget.id, qty); setAdded(true) }}
        >
          Add to cart
        </button>
      </div>
      {added && <p className="ok">Added to cart. <Link to="/cart">View cart →</Link></p>}
    </section>
  )
}
