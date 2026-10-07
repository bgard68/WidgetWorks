import { Link } from 'react-router-dom'
import { AccountNav } from '../components/AccountNav'
import { useProfile } from '../account/useProfile'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { OrderSummary } from '../api/types'
import { money } from '../lib/format'
import { StatusPill } from '../components/StatusPill'
import { PanelSkeleton } from '../components/Skeleton'

const dateFmt = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' })

/**
 * Account home.
 *
 * The main panel is recent orders, not a grid of links. That is deliberate and copied from Company B:
 * their account landing page leads with purchase history, because finding an order is overwhelmingly
 * why anyone opens an account page at all. Company A uses a tile grid instead, which works when you
 * have nine destinations — with two, tiles would read as a page someone forgot to finish.
 */
export function AccountPage() {
  const { profile } = useProfile()
  const [orders, setOrders] = useState<OrderSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api<OrderSummary[]>('/orders').then(setOrders).catch((e) => setError(e.message))
  }, [])

  const recent = orders?.slice(0, 3) ?? []

  return (
    <div className="acct">
      <AccountNav profile={profile} current="home" />

      <div className="acct-main">
        <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline' }}>
          <h1>Your orders</h1>
          {orders && orders.length > 3 && <Link to="/orders" className="link">View all orders</Link>}
        </div>

        {error && <p className="alert alert-err">{error}</p>}

        {!orders && !error && <PanelSkeleton lines={4} />}

        {orders && orders.length === 0 && (
          <div className="empty">
            <span className="empty-ico" aria-hidden="true">🧾</span>
            <h2>No orders yet</h2>
            <p>When you buy something it will show up here, with tracking.</p>
            <Link to="/store" className="btn btn-primary">Start shopping</Link>
          </div>
        )}

        {recent.map((o) => (
          <div key={o.id} className="panel acct-order">
            {/* Metadata strip above, status as the headline below — Company A's order-card shape. The
                status is what someone is scanning for, so it gets the prominence, not the id. */}
            <div className="acct-order-meta">
              <div><span>Order placed</span>{dateFmt.format(new Date(o.createdAt))}</div>
              <div><span>Total</span>{money(o.total)}</div>
              <div className="acct-order-num"><span>Order</span>{o.orderNumber}</div>
            </div>
            <div className="acct-order-body">
              <div>
                <StatusPill status={o.status} />
                <p className="small muted" style={{ marginTop: 6 }}>
                  {o.itemCount} {o.itemCount === 1 ? 'item' : 'items'}
                </p>
              </div>
              <Link to={`/orders/${o.id}`} className="btn btn-secondary btn-sm">View order</Link>
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
