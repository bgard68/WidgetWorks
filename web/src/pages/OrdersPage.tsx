import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { OrderSummary } from '../api/types'
import { money } from '../lib/format'
import { StatusPill } from '../components/StatusPill'
import { PanelSkeleton } from '../components/Skeleton'

const dateFmt = new Intl.DateTimeFormat('en-US', { month: 'long', day: 'numeric', year: 'numeric' })

export function OrdersPage() {
  const [orders, setOrders] = useState<OrderSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api<OrderSummary[]>('/orders').then(setOrders).catch((e) => setError(e.message))
  }, [])

  if (error) return <p className="alert alert-err">{error}</p>

  if (!orders) {
    return (
      <>
        <div className="pagehead"><h1>Your orders</h1></div>
        <PanelSkeleton lines={5} />
      </>
    )
  }

  return (
    <>
      <nav className="crumbs" aria-label="Breadcrumb">
        <Link to="/store" className="link">Home</Link>
        <span className="sep" aria-hidden="true">›</span>
        <span className="cur">Your orders</span>
      </nav>

      <div className="pagehead">
        <div>
          <h1>Your orders</h1>
          <p>{orders.length} {orders.length === 1 ? 'order' : 'orders'} placed with this account.</p>
        </div>
        <Link to="/store" className="btn btn-secondary">Continue shopping</Link>
      </div>

      {orders.length === 0 ? (
        <div className="empty">
          <span className="empty-ico" aria-hidden="true">📦</span>
          <h2>No orders yet</h2>
          <p>When you place an order it will appear here with its status and tracking.</p>
          <Link to="/store" className="btn btn-primary">Start shopping</Link>
        </div>
      ) : (
        <div className="orderlist">
          {orders.map((o) => (
            <article key={o.id} className="ordercard">
              <div className="ordercard-head">
                <div className="f">
                  <span className="k">Order placed</span>
                  <span className="v">{dateFmt.format(new Date(o.createdAt))}</span>
                </div>
                <div className="f">
                  <span className="k">Total</span>
                  <span className="v">{money(o.total)}</span>
                </div>
                <div className="f">
                  <span className="k">Items</span>
                  <span className="v">{o.itemCount}</span>
                </div>
                <div className="right">
                  <div className="f">
                    <span className="k">Order #</span>
                    <span className="v">{o.orderNumber}</span>
                  </div>
                </div>
              </div>
              <div className="ordercard-body">
                <StatusPill status={o.status} />
                <Link to={`/orders/${o.id}`} className="btn btn-secondary btn-sm">
                  View order details
                </Link>
              </div>
            </article>
          ))}
        </div>
      )}
    </>
  )
}
