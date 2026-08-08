import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { OrderSummary } from '../api/types'
import { money } from '../lib/format'

export function OrdersPage() {
  const [orders, setOrders] = useState<OrderSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api<OrderSummary[]>('/orders').then(setOrders).catch((e) => setError(e.message))
  }, [])

  if (error) return <p className="error">{error}</p>
  if (!orders) return <p>Loading…</p>

  return (
    <section>
      <h1>My orders</h1>
      {orders.length === 0 && <p className="muted">No orders yet.</p>}
      <table className="table">
        <tbody>
          {orders.map((o) => (
            <tr key={o.id}>
              <td><Link to={`/orders/${o.id}`}>{o.orderNumber}</Link></td>
              <td>{new Date(o.createdAt).toLocaleDateString()}</td>
              <td>{o.status}</td>
              <td>{o.itemCount} item(s)</td>
              <td>{money(o.total)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}
