import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useCart } from '../cart/CartContext'
import { money } from '../lib/format'
import { FREE_SHIPPING_THRESHOLD } from '../lib/catalog'
import { Price } from '../components/Price'
import { ProductImage } from '../components/ProductImage'

export function CartPage() {
  const { cart, updateItem, removeItem } = useCart()
  const navigate = useNavigate()
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function run(widgetId: string, action: () => Promise<void>) {
    setPending(widgetId)
    setError(null)
    try {
      await action()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not update the cart.')
    } finally {
      setPending(null)
    }
  }

  if (!cart || cart.items.length === 0) {
    return (
      <div className="empty">
        <span className="empty-ico" aria-hidden="true">🛒</span>
        <h2>Your cart is empty</h2>
        <p>Once you add widgets they will show up here, ready for checkout.</p>
        <Link to="/" className="btn btn-primary">Start shopping</Link>
      </div>
    )
  }

  const remaining = FREE_SHIPPING_THRESHOLD - cart.subtotal

  return (
    <>
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/" className="link">Home</Link>
        <span className="sep" aria-hidden="true">›</span>
        <span className="cur">Shopping cart</span>
      </nav>

      {error && <p className="alert alert-err" style={{ marginBottom: 14 }}>{error}</p>}

      <div className="cartlayout">
        <section className="cart-main">
          <div className="cart-head">
            <h1>Shopping cart</h1>
            <span className="muted small">
              {cart.itemCount} {cart.itemCount === 1 ? 'item' : 'items'}
            </span>
          </div>

          <div className="cart-lines">
            {cart.items.map((line) => {
              const busy = pending === line.widgetId
              const href = `/widgets/${line.widgetId}`
              return (
                <div key={line.widgetId} className="cline">
                  {/* Cart lines carry no image URL, so the thumbnail resolves from the
                      SKU the same way the catalog grid does. */}
                  <Link to={href} className="cline-media" tabIndex={-1} aria-hidden="true">
                    <ProductImage sku={line.sku} />
                  </Link>

                  <div className="cline-info">
                    <Link to={href} className="cline-name">{line.name}</Link>
                    <span className="cline-sku">SKU {line.sku}</span>
                    {line.quantityAvailable <= 10 && (
                      <span className="pill pill-warn">Only {line.quantityAvailable} left</span>
                    )}

                    <div className="cline-controls">
                      <div className="qtybox">
                        <button
                          type="button"
                          aria-label={`Decrease quantity of ${line.name}`}
                          disabled={busy || line.quantity <= 1}
                          onClick={() => run(line.widgetId, () => updateItem(line.widgetId, line.quantity - 1))}
                        >−</button>
                        <span className="val" aria-live="polite">{line.quantity}</span>
                        <button
                          type="button"
                          aria-label={`Increase quantity of ${line.name}`}
                          disabled={busy || line.quantity >= line.quantityAvailable}
                          onClick={() => run(line.widgetId, () => updateItem(line.widgetId, line.quantity + 1))}
                        >+</button>
                      </div>

                      <button
                        type="button"
                        className="btn-link"
                        disabled={busy}
                        onClick={() => run(line.widgetId, () => removeItem(line.widgetId))}
                      >
                        {busy ? 'Updating…' : 'Remove'}
                      </button>
                    </div>
                  </div>

                  <div className="cline-money">
                    <Price value={line.lineSubtotal} size="md" />
                    {line.quantity > 1 && (
                      <span className="cline-unit">{money(line.unitPrice)} each</span>
                    )}
                  </div>
                </div>
              )
            })}
          </div>

          <div className="cart-foot">
            <span>Subtotal ({cart.itemCount} {cart.itemCount === 1 ? 'item' : 'items'}):</span>
            <Price value={cart.subtotal} size="md" />
          </div>
        </section>

        <aside className="cart-aside">
          <div className="panel">
            <div className="panel-body">
              {remaining > 0 ? (
                <div className="free-ship">
                  <span aria-hidden="true">🚚</span>
                  <span>Add <strong>{money(remaining)}</strong> more to qualify for free standard shipping.</span>
                </div>
              ) : (
                <div className="free-ship">
                  <span aria-hidden="true">✓</span>
                  <span>Your order qualifies for <strong>free standard shipping</strong>.</span>
                </div>
              )}

              <div className="sumrow total">
                <span>Subtotal</span>
                <span>{money(cart.subtotal)}</span>
              </div>
              <p className="help">Shipping and tax are calculated at checkout.</p>

              <button className="btn btn-buy btn-block btn-lg" onClick={() => navigate('/checkout')}>
                Proceed to checkout
              </button>
              <Link to="/" className="btn btn-secondary btn-block">Continue shopping</Link>
            </div>
          </div>
        </aside>
      </div>
    </>
  )
}
