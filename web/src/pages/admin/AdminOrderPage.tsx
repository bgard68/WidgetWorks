import { useState } from 'react'
import { api } from '../../api/client'
import type { OrderView } from '../../api/types'
import { money } from '../../lib/format'
import { StatusPill } from '../../components/StatusPill'

export function AdminOrderPage() {
  const [orderId, setOrderId] = useState('')
  const [order, setOrder] = useState<OrderView | null>(null)
  const [tracking, setTracking] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function lookup(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const o = await api<OrderView>(`/admin/orders/${orderId.trim()}`)
      setOrder(o)
      setTracking(o.trackingNumber ?? '')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Not found.')
      setOrder(null)
    } finally {
      setBusy(false)
    }
  }

  async function setStatus(status: string) {
    if (!order) return
    setError(null)
    setBusy(true)
    try {
      const o = await api<OrderView>(`/admin/orders/${order.id}/status`, {
        method: 'POST',
        body: { status, trackingNumber: tracking || null },
      })
      setOrder(o)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Update failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="pagehead">
        <div>
          <div className="admin-head">
            <span className="admin-tag">Admin</span>
            <h1>Orders</h1>
          </div>
          <p>Look up an order by its id to update fulfilment status and tracking.</p>
        </div>
      </div>

      <div className="panel" style={{ marginBottom: 18 }}>
        <div className="panel-body">
          <form onSubmit={lookup} className="admin-form">
            <label className="field wide">
              <span>Order id (GUID)</span>
              <input
                placeholder="00000000-0000-0000-0000-000000000000"
                value={orderId}
                onChange={(e) => setOrderId(e.target.value)}
                required
              />
            </label>
            <button className="btn btn-solid" disabled={busy}>{busy ? 'Looking up…' : 'Look up order'}</button>
          </form>
        </div>
      </div>

      {error && <p className="alert alert-err">{error}</p>}

      {order && (
        <div className="panel">
          <div className="panel-head">
            <div className="row" style={{ justifyContent: 'space-between' }}>
              <h2>{order.orderNumber}</h2>
              <StatusPill status={order.status} />
            </div>
          </div>
          <div className="panel-body stack">
            <div className="ordercard-head" style={{ background: 'transparent', border: 0, padding: 0 }}>
              <div className="f"><span className="k">Customer</span><span className="v">{order.email}</span></div>
              <div className="f"><span className="k">Total</span><span className="v">{money(order.total)}</span></div>
              <div className="f"><span className="k">Items</span><span className="v">{order.items.length}</span></div>
              <div className="f"><span className="k">Shipping</span><span className="v">{order.shippingMethod}</span></div>
            </div>

            <label className="field" style={{ maxWidth: 320 }}>
              <span>Tracking number</span>
              <input value={tracking} onChange={(e) => setTracking(e.target.value)} placeholder="1Z999AA10123456784" />
            </label>

            <div className="row">
              <button className="btn btn-solid" disabled={busy} onClick={() => setStatus('Shipped')}>Mark shipped</button>
              <button className="btn btn-secondary" disabled={busy} onClick={() => setStatus('Delivered')}>Mark delivered</button>
              <button className="btn btn-danger" disabled={busy} onClick={() => setStatus('Cancelled')}>Cancel order</button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
