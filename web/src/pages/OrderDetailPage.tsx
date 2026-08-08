import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { OrderView } from '../api/types'
import { money } from '../lib/format'

export function OrderDetailPage() {
  const { id } = useParams<{ id: string }>()
  const [order, setOrder] = useState<OrderView | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!id) return
    api<OrderView>(`/orders/${id}`).then(setOrder).catch((e) => setError(e.message))
  }, [id])

  if (error) return <p className="error">{error}</p>
  if (!order) return <p>Loading…</p>

  return (
    <section>
      <Link to="/orders" className="muted">← My orders</Link>
      <h1>Order {order.orderNumber}</h1>
      <p>Status: <strong>{order.status}</strong>{order.trackingNumber ? ` · Tracking ${order.trackingNumber}` : ''}</p>
      <table className="table">
        <thead><tr><th>Widget</th><th>Qty</th><th>Price</th><th>Subtotal</th></tr></thead>
        <tbody>
          {order.items.map((i) => (
            <tr key={i.widgetId}><td>{i.name}</td><td>{i.quantity}</td><td>{money(i.unitPrice)}</td><td>{money(i.lineSubtotal)}</td></tr>
          ))}
        </tbody>
      </table>
      <div className="summary">
        <div>Subtotal <span>{money(order.subtotal)}</span></div>
        <div>Shipping ({order.shippingMethod}) <span>{money(order.shipping)}</span></div>
        <div>Tax <span>{money(order.tax)}</span></div>
        <div className="total">Total <span>{money(order.total)}</span></div>
      </div>
    </section>
  )
}
