import { useCallback, useEffect, useState } from 'react'
import { api } from '../../api/client'
import type { OrderSummary, OrderView, PaymentExceptions } from '../../api/types'
import { money } from '../../lib/format'
import { StatusPill } from '../../components/StatusPill'
import { PanelSkeleton } from '../../components/Skeleton'

const dateFmt = new Intl.DateTimeFormat('en-US', {
  month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
})

export function AdminOrderPage() {
  const [orders, setOrders] = useState<OrderSummary[] | null>(null)
  const [exceptions, setExceptions] = useState<PaymentExceptions | null>(null)
  const [order, setOrder] = useState<OrderView | null>(null)
  const [tracking, setTracking] = useState('')
  const [refundAmount, setRefundAmount] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // The list is the entry point. Looking an order up by GUID was the only way in before, and
  // nobody has a GUID to hand — so staff could not actually find an order.
  const loadList = useCallback(() => {
    api<OrderSummary[]>('/admin/orders')
      .then(setOrders)
      .catch((e) => setError(e.message))

    // Loaded with the list rather than hidden behind a tab. An order holding stock over an unknown
    // payment, or a customer charged twice, is not something staff should have to go looking for —
    // and on a healthy day it renders nothing at all.
    api<PaymentExceptions>('/admin/orders/payment-exceptions')
      .then(setExceptions)
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

  async function refund() {
    /* v8 ignore next -- the refund button only renders in the branch where an order loaded */
    if (!order) return
    setError(null)
    setBusy(true)
    try {
      // An empty box means the whole remaining balance, which is what a staff member clicking the
      // button expects. A figure typed in is a partial refund.
      const amount = refundAmount.trim() ? Number(refundAmount) : null
      const o = await api<OrderView>(`/admin/orders/${order.id}/refund`, { method: 'POST', body: { amount } })
      setOrder(o)
      setRefundAmount('')
      loadList()
    } catch (err) {
      // A refund the provider could not confirm lands here. The server's message says to check the
      // provider rather than to try again, because a second attempt could pay out twice.
      setError(err instanceof Error ? err.message : 'Refund failed.')
    } finally {
      setBusy(false)
    }
  }

  async function setStatus(status: string) {
    /* v8 ignore next -- the buttons that call this only render in the branch where an order loaded */
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

      {exceptions && exceptions.unconfirmed.length > 0 && (
        <div className="alert alert-err" style={{ marginBottom: 14 }}>
          <strong>{exceptions.unconfirmed.length} order(s) with an unconfirmed payment.</strong>{' '}
          The provider never said whether these were charged, so each is holding its stock until
          reconciliation finds out. If one stays here, check the provider and settle it by hand.
          <ul className="bare small" style={{ marginTop: 6 }}>
            {exceptions.unconfirmed.map((o) => (
              <li key={o.id}>
                <button type="button" className="link" disabled={busy} onClick={() => open(o.id)}>{o.orderNumber}</button>
                {` — ${money(o.total)}`}
                {o.email ? ` · ${o.email}` : ''}
                {o.paymentUnconfirmedAt ? ` · since ${dateFmt.format(new Date(o.paymentUnconfirmedAt))}` : ''}
              </li>
            ))}
          </ul>
        </div>
      )}

      {exceptions && exceptions.possibleDuplicates.length > 0 && (
        <div className="alert alert-warn" style={{ marginBottom: 14 }}>
          <strong>{exceptions.possibleDuplicates.length} order(s) look like duplicates.</strong>{' '}
          Same customer, same total, placed close together. A guess rather than a verdict — open them
          and refund one if it was a mistake.
          <ul className="bare small" style={{ marginTop: 6 }}>
            {exceptions.possibleDuplicates.map((o) => (
              <li key={o.id}>
                <button type="button" className="link" disabled={busy} onClick={() => open(o.id)}>{o.orderNumber}</button>
                {` — ${money(o.total)}`}
                {o.email ? ` · ${o.email}` : ''}
                {` · ${dateFmt.format(new Date(o.createdAt))}`}
              </li>
            ))}
          </ul>
        </div>
      )}

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

                {order.paymentUnconfirmedAt && (
                  <p className="alert alert-err small">
                    The provider never confirmed this charge. The order is holding stock and cannot
                    ship until reconciliation settles it.
                  </p>
                )}

                <div className="row">
                  <button className="btn btn-solid btn-sm" disabled={busy} onClick={() => setStatus('Shipped')}>Mark shipped</button>
                  <button className="btn btn-secondary btn-sm" disabled={busy} onClick={() => setStatus('Delivered')}>Delivered</button>
                  <button className="btn btn-danger btn-sm" disabled={busy} onClick={() => setStatus('Cancelled')}>Cancel</button>
                </div>

                {/* Only a paid order can be refunded here: once it ships, money is half the question
                    and the rest is a returns workflow. Hiding the button says so before the API has to. */}
                {order.refundedTotal > 0 && (
                  <div className="sumrow"><span>Refunded</span><span>{money(order.refundedTotal)}</span></div>
                )}

                {order.status === 'Paid' && (
                  <>
                    <label className="field">
                      <span>Refund amount</span>
                      <input
                        value={refundAmount}
                        onChange={(e) => setRefundAmount(e.target.value)}
                        placeholder={`Blank refunds the full ${money(order.total - order.refundedTotal)}`}
                      />
                    </label>
                    <div className="row">
                      <button className="btn btn-danger btn-sm" disabled={busy} onClick={refund}>Refund order</button>
                    </div>
                  </>
                )}

                <p className="help">
                  Marking shipped or cancelled emails the customer.
                  {order.status === 'Paid' && ' Refunding returns the full total and puts the stock back on sale.'}
                </p>
              </div>
            </div>
          )}
        </aside>
      </div>
    </>
  )
}
