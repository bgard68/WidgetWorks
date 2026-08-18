import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { WidgetView } from '../api/types'
import { pseudoRating } from '../lib/img'
import { FREE_SHIPPING_THRESHOLD } from '../lib/catalog'
import { AddToCartButton } from '../components/AddToCartButton'
import { ProductImage } from '../components/ProductImage'
import { Price } from '../components/Price'
import { Rating } from '../components/Stars'
import { PanelSkeleton } from '../components/Skeleton'

const USPS = [
  { ico: '🚚', text: `Free standard shipping on orders over $${FREE_SHIPPING_THRESHOLD}.` },
  { ico: '↩️', text: '30-day returns — send it back if it is not the right widget.' },
  { ico: '🔒', text: 'Secure checkout with card, Google Pay or Klarna.' },
]

export function ProductPage() {
  const { id } = useParams<{ id: string }>()
  const [widget, setWidget] = useState<WidgetView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [qty, setQty] = useState(1)
  const [added, setAdded] = useState(false)

  useEffect(() => {
    if (!id) return
    setWidget(null)
    setError(null)
    api<WidgetView>(`/catalog/widgets/${id}`)
      .then(setWidget)
      .catch((e) => setError(e.message))
  }, [id])

  if (error) {
    return (
      <div className="empty">
        <span className="empty-ico" aria-hidden="true">📦</span>
        <h2>We couldn&apos;t load that widget</h2>
        <p>{error}</p>
        <Link to="/" className="btn btn-secondary">Back to the shop</Link>
      </div>
    )
  }

  if (!widget) {
    return (
      <>
        <nav className="crumbs" aria-label="Breadcrumb"><Link to="/" className="link">Home</Link></nav>
        <PanelSkeleton lines={6} />
      </>
    )
  }

  const { rating, reviews } = pseudoRating(widget.sku)
  const out = widget.quantityAvailable <= 0
  const low = !out && widget.quantityAvailable <= 10
  const maxQty = Math.min(10, Math.max(1, widget.quantityAvailable))
  const freeShipping = widget.price * qty >= FREE_SHIPPING_THRESHOLD

  return (
    <>
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/" className="link">Home</Link>
        <span className="sep" aria-hidden="true">›</span>
        <Link to="/" className="link">All widgets</Link>
        <span className="sep" aria-hidden="true">›</span>
        <span className="cur">{widget.name}</span>
      </nav>

      <div className="pdp">
        {/* Image ------------------------------------------------------- */}
        <div className="pdp-media">
          <div className="frame">
            <ProductImage sku={widget.sku} imageUrl={widget.imageUrl} alt={widget.name} />
          </div>
          <p className="zoomnote">Sample product photography</p>
        </div>

        {/* Details ----------------------------------------------------- */}
        <div className="pdp-main">
          <h1 className="pdp-title">{widget.name}</h1>

          <div className="pdp-meta">
            <Rating rating={rating} reviews={reviews} />
            <span className="pdp-sku">SKU {widget.sku}</span>
          </div>

          <div className="pdp-price-row">
            <Price value={widget.price} size="lg" />
            <span className="muted small">Price includes all applicable duties</span>
          </div>

          <section className="pdp-section">
            <h2>About this widget</h2>
            <p>{widget.description}</p>
          </section>

          <section className="pdp-section">
            <h2>Product details</h2>
            <dl className="pdp-specs">
              <dt>SKU</dt><dd>{widget.sku}</dd>
              <dt>Availability</dt>
              <dd>{out ? 'Out of stock' : `${widget.quantityAvailable} available`}</dd>
              <dt>Ships from</dt><dd>WidgetWorks</dd>
              <dt>Sold by</dt><dd>WidgetWorks</dd>
            </dl>
          </section>

          <section className="pdp-section">
            <h2>Why shop with us</h2>
            <div className="pdp-usps">
              {USPS.map((u) => (
                <div key={u.text} className="pdp-usp">
                  <span className="ico" aria-hidden="true">{u.ico}</span>
                  <span>{u.text}</span>
                </div>
              ))}
            </div>
          </section>
        </div>

        {/* Buy box ----------------------------------------------------- */}
        <aside className="buybox" aria-label="Purchase options">
          <Price value={widget.price} size="md" />

          <p className="ship">
            {freeShipping
              ? <><b>FREE</b> standard shipping on this order.</>
              : <>Free standard shipping on orders over <b>${FREE_SHIPPING_THRESHOLD}</b>.</>}
          </p>

          <div className={out ? 'stock-out' : low ? 'stock-low' : 'stock-ok'}>
            {out ? 'Currently unavailable' : low ? `Only ${widget.quantityAvailable} left in stock` : 'In stock'}
          </div>

          {!out && (
            <div className="buy-qty">
              <label htmlFor="qty">Qty</label>
              <select id="qty" value={qty} onChange={(e) => setQty(Number(e.target.value))}>
                {Array.from({ length: maxQty }, (_, i) => i + 1).map((n) => (
                  <option key={n} value={n}>{n}</option>
                ))}
              </select>
            </div>
          )}

          <AddToCartButton
            widgetId={widget.id}
            quantity={qty}
            outOfStock={out}
            onAdded={() => setAdded(true)}
          />

          {added && (
            <p className="added-note">
              <span aria-hidden="true">✓</span>
              Added — <Link to="/cart" className="link">view cart</Link>
            </p>
          )}

          <div className="buybox-secure">
            <span aria-hidden="true">🔒</span> Secure transaction · Ships from WidgetWorks
          </div>
        </aside>
      </div>
    </>
  )
}
