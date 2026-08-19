import { useCallback, useEffect, useState } from 'react'
import { api } from '../../api/client'
import type { OrderSummary, OrderView } from '../../api/types'
import { money } from '../../lib/format'
import { StatusPill } from '../../components/StatusPill'
import { PanelSkeleton } from '../../components/Skeleton'

const dateFmt = new Intl.DateTimeFormat('en-US', {
  month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
})

export function AdminOrderPage() {
  const [orders, setOrders] = useState<OrderSummary[] | null>(null)
  const [order, setOrder] = useState<OrderView | null>(null)
  const [tracking, setTracking] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // The list is the entry point. Looking an order up by GUID was the only way in before, and
  // nobody has a GUID to hand — so staff could not actually find an order.
  const loadList = useCallback(() => {
    api<OrderSummary[]>('/admin/orders')
      .then(setOrders)
      .catch((e) => setError(e.message))
  }, [])

  useEffect(() => { loadList() }, [loadList])

  async function open(id: string) {
    setError(null)
    setBusy(true)
    try {
      const o = await api<OrderView>(`/admin/orders/${id}`)
      setOrder(o)
      setTracking(o.trackingNumber ?? '')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load that order.')
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
      loadList()
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
          <p>
            {orders ? `${orders.length} most recent ${orders.length === 1 ? 'order' : 'orders'}.` : 'Loading orders…'}
            {' '}Select one to update its fulfilment status and tracking.
          </p>
        </div>
        <button type="button" className="btn btn-secondary" onClick={loadList} disabled={busy}>Refresh</button>
      </div>

      {error && <p className="alert alert-err" style={{ marginBottom: 14 }}>{error}</p>}

      <div className="confirm-grid">
        <div>
          {!orders ? (
            <PanelSkeleton lines={6} />
          ) : orders.length === 0 ? (
            <div className="empty">
              <span className="empty-ico" aria-hidden="true">🧾</span>
              <h2>No orders yet</h2>
              <p>Orders placed in the store will appear here.</p>
            </div>
          ) : (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Order</th><th>Placed</th><th>Status</th>
                    <th className="num">Items</th><th className="num">Total</th><th></th>
                  </tr>
                </thead>
                <tbody>
                  {orders.map((o) => (
                    <tr key={o.id} className={order?.id === o.id ? 'on' : undefined}>
                      <td className="strong nums">{o.orderNumber}</td>
                      <td className="small muted">{dateFmt.format(new Date(o.createdAt))}</td>
                      <td><StatusPill status={o.status} /></td>
                      <td className="num nums">{o.itemCount}</td>
                      <td className="num nums">{money(o.total)}</td>
                      <td>
                        <button type="button" className="btn btn-secondary btn-sm" disabled={busy} onClick={() => open(o.id)}>
                          Open
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        <aside className="summary">
          {!order ? (
            <div className="panel">
              <div className="panel-body">
                <h3>No order selected</h3>
                <p className="muted small" style={{ marginTop: 6 }}>
                  Pick an order from the list to see its detail and change its status.
                </p>
              </div>
            </div>
          ) : (
            <div className="panel">
              <div className="panel-head">
                <div className="row" style={{ justifyContent: 'space-between' }}>
                  <h2>{order.orderNumber}</h2>
                  <StatusPill status={order.status} />
                </div>
              </div>
              <div className="panel-body stack">
                <div className="sumrow"><span>Customer</span><span>{order.email}</span></div>
                <div className="sumrow"><span>Shipping</span><span>{order.shippingMethod}</span></div>
                <div className="sumrow"><span>Items</span><span>{order.items.length}</span></div>
                <div className="sumrow total"><span>Total</span><span>{money(order.total)}</span></div>

                <label className="field">
                  <span>Tracking number</span>
                  <input value={tracking} onChange={(e) => setTracking(e.target.value)} placeholder="1Z999AA10123456784" />
                </label>

                <div className="row">
                  <button className="btn btn-solid btn-sm" disabled={busy} onClick={() => setStatus('Shipped')}>Mark shipped</button>
                  <button className="btn btn-secondary btn-sm" disabled={busy} onClick={() => setStatus('Delivered')}>Delivered</button>
                  <button className="btn btn-danger btn-sm" disabled={busy} onClick={() => setStatus('Cancelled')}>Cancel</button>
                </div>
                <p className="help">Marking shipped or cancelled emails the customer.</p>
              </div>
            </div>
          )}
        </aside>
      </div>
    </>
  )
}
