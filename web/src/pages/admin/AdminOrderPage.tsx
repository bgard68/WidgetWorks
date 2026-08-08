import { useState } from 'react'
import { api } from '../../api/client'
import type { OrderView } from '../../api/types'
import { money } from '../../lib/format'

export function AdminOrderPage() {
  const [orderId, setOrderId] = useState('')
  const [order, setOrder] = useState<OrderView | null>(null)
  const [tracking, setTracking] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function lookup(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      const o = await api<OrderView>(`/admin/orders/${orderId.trim()}`)
      setOrder(o)
      setTracking(o.trackingNumber ?? '')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Not found.')
      setOrder(null)
    }
  }

  async function setStatus(status: string) {
    if (!order) return
    setError(null)
    try {
      const o = await api<OrderView>(`/admin/orders/${order.id}/status`, {
        method: 'POST',
        body: { status, trackingNumber: tracking || null },
      })
      setOrder(o)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Update failed.')
    }
  }

  return (
    <section>
      <h1>Admin · Orders</h1>
      <form onSubmit={lookup} className="form inline">
        <input placeholder="Order ID (GUID)" value={orderId} onChange={(e) => setOrderId(e.target.value)} />
        <button>Look up</button>
      </form>
      {error && <p className="error">{error}</p>}
      {order && (
        <div>
          <h2>{order.orderNumber} — {order.status}</h2>
          <p>{order.email} · {money(order.total)}</p>
          <label>Tracking<input value={tracking} onChange={(e) => setTracking(e.target.value)} /></label>
          <div className="row">
            <button onClick={() => setStatus('Shipped')}>Mark shipped</button>
            <button onClick={() => setStatus('Delivered')}>Mark delivered</button>
            <button onClick={() => setStatus('Cancelled')}>Cancel</button>
          </div>
        </div>
      )}
    </section>
  )
}
