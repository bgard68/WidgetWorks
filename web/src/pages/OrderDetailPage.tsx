import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { OrderView } from '../api/types'
import { money } from '../lib/format'
import { ProductImage } from '../components/ProductImage'
import { StatusPill } from '../components/StatusPill'
import { PanelSkeleton } from '../components/Skeleton'

const dateFmt = new Intl.DateTimeFormat('en-US', {
  month: 'long', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
})

export function OrderDetailPage() {
  const { id } = useParams<{ id: string }>()
  const [order, setOrder] = useState<OrderView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!id) return
    api<OrderView>(`/orders/${id}`).then(setOrder).catch((e) => setError(e.message))
  }, [id])

  if (error) return <p className="alert alert-err">{error}</p>
  if (!order) return <PanelSkeleton lines={6} />

  return (
    <>
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/" className="link">Home</Link>
        <span className="sep" aria-hidden="true">›</span>
        <Link to="/orders" className="link">Your orders</Link>
        <span className="sep" aria-hidden="true">›</span>
        <span className="cur">{order.orderNumber}</span>
      </nav>

      <div className="pagehead">
        <div>
          <h1>Order {order.orderNumber}</h1>
          <p>Placed {dateFmt.format(new Date(order.createdAt))}</p>
        </div>
        <StatusPill status={order.status} />
      </div>

      <div className="confirm-grid">
        <div className="stack">
          {order.trackingNumber && (
            <div className="panel">
              <div className="panel-body">
                <h3>Tracking</h3>
                <p className="muted small" style={{ marginTop: 4 }}>
                  Carrier reference <strong className="strong">{order.trackingNumber}</strong>
                </p>
              </div>
            </div>
          )}

          <div className="panel">
            <div className="panel-head"><h2>Items in this order</h2></div>
            <div className="cart-lines" style={{ padding: '0 18px' }}>
              {order.items.map((i) => (
                <div key={i.widgetId} className="cline">
                  <Link to={`/widgets/${i.widgetId}`} className="cline-media" tabIndex={-1} aria-hidden="true">
                    <ProductImage sku={i.sku} />
                  </Link>
                  <div className="cline-info">
                    <Link to={`/widgets/${i.widgetId}`} className="cline-name">{i.name}</Link>
                    <span className="cline-sku">SKU {i.sku}</span>
                    <span className="muted small">Qty {i.quantity} · {money(i.unitPrice)} each</span>
                  </div>
                  <div className="cline-money">
                    <span className="strong">{money(i.lineSubtotal)}</span>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>

        <aside className="summary">
          <div className="panel">
            <div className="panel-head"><h2>Order summary</h2></div>
            <div className="panel-body">
              <div className="sumrow"><span>Subtotal</span><span>{money(order.subtotal)}</span></div>
              <div className={`sumrow${order.shipping === 0 ? ' free' : ''}`}>
                <span>Shipping ({order.shippingMethod})</span>
                <span>{order.shipping === 0 ? 'FREE' : money(order.shipping)}</span>
              </div>
              <div className="sumrow">
                <span>Tax {order.taxState ? `(${order.taxState})` : ''}</span>
                <span>{money(order.tax)}</span>
              </div>
              <div className="sumrow total"><span>Order total</span><span>{money(order.total)}</span></div>

              {order.paymentProvider && (
                <p className="help" style={{ marginTop: 14 }}>
                  Paid with {order.paymentProvider}
                  {order.paymentReference ? ` · ref ${order.paymentReference}` : ''}
                </p>
              )}
            </div>
            <div className="panel-foot">
              <Link to="/orders" className="link">← Back to your orders</Link>
            </div>
          </div>
        </aside>
      </div>
    </>
  )
}
