import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { api } from '../api/client'
import type { WidgetView } from '../api/types'
import { money } from '../lib/format'
import { productImage, pseudoRating } from '../lib/img'
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

  const { rating, reviews } = pseudoRating(widget.sku)
  const out = widget.quantityAvailable <= 0

  return (
    <section className="product">
      <Link to="/" className="muted">← Back to shop</Link>
      <div className="product-grid">
        <div className="product-photo">
          <img src={productImage(widget)} alt={widget.name}
               onError={(e) => { (e.currentTarget as HTMLImageElement).style.visibility = 'hidden' }} />
        </div>
        <div>
          <h1>{widget.name}</h1>
          <div className="stars">{'★'.repeat(Math.round(rating))}{'☆'.repeat(5 - Math.round(rating))} <span className="muted">{rating.toFixed(1)} · {reviews.toLocaleString()} reviews</span></div>
          <p>{widget.description}</p>
          <div className="price big">{money(widget.price)}</div>
          <div className={out ? 'error' : 'instock'}>{out ? 'Out of stock' : `${widget.quantityAvailable} in stock`}</div>
          <div className="row buy">
            <input type="number" min={1} max={Math.max(1, widget.quantityAvailable)} value={qty}
                   onChange={(e) => setQty(Math.max(1, Number(e.target.value)))} />
            <button className="add" disabled={out} onClick={async () => { await addItem(widget.id, qty); setAdded(true) }}>
              Add to cart
            </button>
          </div>
          {added && <p className="ok">Added to cart. <Link to="/cart">View cart →</Link></p>}
        </div>
      </div>
    </section>
  )
}
