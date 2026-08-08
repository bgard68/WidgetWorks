import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Paged, WidgetView } from '../api/types'
import { money } from '../lib/format'
import { productImage, pseudoRating } from '../lib/img'
import { useCart } from '../cart/CartContext'

function Stars({ rating }: { rating: number }) {
  const full = Math.round(rating)
  return <span className="s">{'★'.repeat(full)}{'☆'.repeat(5 - full)}</span>
}

export function CatalogPage() {
  const [params] = useSearchParams()
  const q = params.get('q') ?? ''
  const [data, setData] = useState<Paged<WidgetView> | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const { addItem } = useCart()

  useEffect(() => {
    let active = true
    setLoading(true)
    const qs = q.trim() ? `?search=${encodeURIComponent(q.trim())}` : ''
    api<Paged<WidgetView>>(`/catalog/widgets${qs}`)
      .then((d) => { if (active) { setData(d); setError(null) } })
      .catch((e) => { if (active) setError(e.message) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [q])

  return (
    <section>
      {!q && (
        <div className="hero">
          <div className="hero-main">
            <h1>Widgets for every job.</h1>
            <p>From the everyday Standard to the heavy-duty Mega — quality widgets, fast shipping, honest prices.</p>
            <Link to="/?q=kit" className="hero-cta">Shop the kits →</Link>
          </div>
          <div className="hero-promo p1"><small>Deals</small><h3>Save on Widget Pro Kits</h3></div>
          <div className="hero-promo p2"><small>New</small><h3>Weatherproof widgets</h3></div>
        </div>
      )}

      <div className="sec"><h2>{q ? `Results for “${q}”` : 'Featured widgets'}</h2></div>

      {loading && <p>Loading…</p>}
      {error && <p className="error">{error}</p>}

      <div className="grid">
        {data?.items.map((w) => {
          const { rating, reviews } = pseudoRating(w.sku)
          const out = w.quantityAvailable <= 0
          const low = !out && w.quantityAvailable <= 10
          return (
            <div key={w.id} className="card">
              <Link to={`/widgets/${w.id}`} className="thumb">
                <img src={productImage(w)} alt={w.name} loading="lazy"
                     onError={(e) => { (e.currentTarget as HTMLImageElement).style.visibility = 'hidden' }} />
              </Link>
              {out ? <span className="badge out">Out of stock</span>
                   : low ? <span className="badge low">Only {w.quantityAvailable} left</span> : null}
              <div className="card-body">
                <Link to={`/widgets/${w.id}`} className="card-title">{w.name}</Link>
                <div className="stars"><Stars rating={rating} /> {rating.toFixed(1)} <span className="muted">({reviews.toLocaleString()})</span></div>
                <div className="muted small desc">{w.description}</div>
                <div className="price">{money(w.price)}</div>
                <button className="add" disabled={out} onClick={() => addItem(w.id, 1)}>
                  {out ? 'Out of stock' : 'Add to cart'}
                </button>
              </div>
            </div>
          )
        })}
      </div>

      {data && data.items.length === 0 && <p className="muted">No widgets found.</p>}
    </section>
  )
}
