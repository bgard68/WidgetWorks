import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { OrderView } from '../api/types'
import { money } from '../lib/format'
import { StatusPill } from '../components/StatusPill'

const dateFmt = new Intl.DateTimeFormat('en-US', { month: 'long', day: 'numeric', year: 'numeric' })

/**
 * Guest order tracking.
 *
 * `GET /orders/lookup` has existed since orders did, with its own rate-limit policy — but nothing in
 * the app ever called it. A guest who checked out had no way to see their order again, which rather
 * undermines offering guest checkout at all.
 *
 * Both fields are required by the server and that is the point of the design: an order number alone
 * would let anyone walk the sequence and read other people's addresses. Requiring the email with it
 * means knowing the order number is not enough.
 *
 * The failure message is deliberately the same whether the order does not exist or the email does not
 * match it — otherwise this becomes a way to test which email placed a given order.
 */
export function TrackOrderPage() {
  const [number, setNumber] = useState('')
  const [email, setEmail] = useState('')
  const [order, setOrder] = useState<OrderView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const found = await api<OrderView>(
        `/orders/lookup?number=${encodeURIComponent(number.trim())}&email=${encodeURIComponent(email.trim())}`,
      )
      setOrder(found)
    } catch (err) {
      setOrder(null)
      setError(err instanceof Error ? err.message : 'Could not find that order.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="authpage">
      <div className="authcard">
        <h1>Track your order</h1>
        <p className="sub">No account needed — just the order number from your confirmation email.</p>

        <form onSubmit={submit}>
          <label className="field">
            <span>Order number</span>
            <input
              required
              value={number}
              onChange={(e) => setNumber(e.target.value)}
              placeholder="WW-20260501-ABC123"
            />
          </label>
          <label className="field">
            <span>Email address</span>
            <input
              type="email"
              required
              autoComplete="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </label>

          {error && <p className="alert alert-err">{error}</p>}

          <button className="btn btn-primary btn-block btn-lg" disabled={busy}>
            {busy ? 'Looking…' : 'Find my order'}
          </button>
        </form>

        {order && (
          <div className="panel" style={{ marginTop: 16 }}>
            <div className="panel-head">
              <div className="row" style={{ justifyContent: 'space-between' }}>
                <h2>{order.orderNumber}</h2>
                <StatusPill status={order.status} />
              </div>
            </div>
            <div className="panel-body stack">
              <div className="sumrow"><span>Placed</span><span>{dateFmt.format(new Date(order.createdAt))}</span></div>
              <div className="sumrow"><span>Items</span><span>{order.items.length}</span></div>
              {order.trackingNumber && (
                <div className="sumrow"><span>Tracking</span><span className="nums">{order.trackingNumber}</span></div>
              )}
              <div className="sumrow total"><span>Total</span><span>{money(order.total)}</span></div>
            </div>
          </div>
        )}
      </div>

      <div className="auth-alt">
        Have an account? <Link to="/login">Sign in</Link> to see all your orders.
      </div>
    </div>
  )
}
