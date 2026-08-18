import { useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { api } from '../api/client'
import type { CheckoutResult } from '../api/types'
import { money } from '../lib/format'
import { StatusPill } from '../components/StatusPill'

type ConfirmState = CheckoutResult & { email?: string }

export function OrderConfirmationPage() {
  const location = useLocation()
  const result = location.state as ConfirmState | null
  const [status, setStatus] = useState(result?.status ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (!result) {
    return (
      <div className="empty">
        <span className="empty-ico" aria-hidden="true">🧾</span>
        <h2>No recent order to show</h2>
        <p>Order confirmations appear here right after checkout. Sign in to look up past orders.</p>
        <div className="row" style={{ justifyContent: 'center' }}>
          <Link to="/" className="btn btn-primary">Back to shop</Link>
          <Link to="/orders" className="btn btn-secondary">Your orders</Link>
        </div>
      </div>
    )
  }

  // Demo helper: in a live store the shopper is redirected to the provider (Klarna, etc.),
  // which later calls our webhook. Here we invoke that same webhook directly to settle the order.
  async function simulate(outcome: 'succeeded' | 'failed') {
    setBusy(true)
    setError(null)
    try {
      const res = await api<{ status: string }>('/webhooks/payments/mock', {
        method: 'POST',
        body: { reference: result!.paymentReference, outcome },
      })
      setStatus(res.status)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not settle the payment.')
    } finally {
      setBusy(false)
    }
  }

  const paid = status === 'Paid'
  const failed = status === 'PaymentFailed'
  const awaiting = status === 'AwaitingPayment'

  const tone = paid ? '' : failed ? ' bad' : ' warn'
  const icon = paid ? '✓' : failed ? '✕' : '⏳'
  const heading = paid ? 'Thank you — your order is confirmed'
    : failed ? 'Payment not completed'
      : 'Almost there — awaiting payment'

  return (
    <>
      <div className={`confirm-hero${tone}`}>
        <span className="confirm-ico" aria-hidden="true">{icon}</span>
        <div>
          <h1>{heading}</h1>
          <p>
            Order <strong>{result.orderNumber}</strong>
            {result.email ? <> · confirmation sent to <strong>{result.email}</strong></> : null}
          </p>
          {paid && <p className="muted small">A confirmation email is on its way. You can track it from Your orders.</p>}
          {failed && (
            <p className="muted small">
              The payment wasn&apos;t completed, so the order was cancelled and your items released.
            </p>
          )}
        </div>
      </div>

      <div className="confirm-grid">
        <div className="stack">
          {awaiting && (
            <div className="panel">
              <div className="panel-head"><h2>Waiting on {result.paymentProvider}</h2></div>
              <div className="panel-body stack">
                <p className="muted">
                  {result.paymentProvider} is processing your payment of {money(result.total)}. In a live
                  store you&apos;d be redirected to the provider to approve it; we&apos;ll email you once it
                  settles. Your items stay reserved until then.
                </p>
                <div className="demo-box">
                  <span className="lbl">Demo — simulate the provider&apos;s webhook callback</span>
                  <div className="row">
                    <button className="btn btn-primary" disabled={busy} onClick={() => simulate('succeeded')}>
                      {busy ? 'Working…' : 'Approve payment'}
                    </button>
                    <button type="button" className="btn btn-danger" disabled={busy} onClick={() => simulate('failed')}>
                      Simulate decline
                    </button>
                  </div>
                </div>
                {error && <p className="alert alert-err">{error}</p>}
              </div>
            </div>
          )}

          {paid && (
            <div className="panel">
              <div className="panel-head"><h2>What happens next</h2></div>
              <div className="panel-body">
                <div className="pdp-usps">
                  <div className="pdp-usp"><span className="ico" aria-hidden="true">📧</span><span>A confirmation email with your receipt is on its way.</span></div>
                  <div className="pdp-usp"><span className="ico" aria-hidden="true">📦</span><span>We&apos;ll pick and pack your widgets, then send tracking details.</span></div>
                  <div className="pdp-usp"><span className="ico" aria-hidden="true">↩️</span><span>Changed your mind? You have 30 days to return it.</span></div>
                </div>
              </div>
            </div>
          )}

          {failed && (
            <div className="panel">
              <div className="panel-body stack">
                <p className="muted">Head back to the shop and try again with a different payment method.</p>
                <div className="row">
                  <Link to="/" className="btn btn-primary">Back to the shop</Link>
                </div>
              </div>
            </div>
          )}
        </div>

        <aside className="summary">
          <div className="panel">
            <div className="panel-head"><h2>Order details</h2></div>
            <div className="panel-body">
              <div className="sumrow"><span>Order number</span><span>{result.orderNumber}</span></div>
              <div className="sumrow"><span>Status</span><span><StatusPill status={status} /></span></div>
              <div className="sumrow"><span>Payment</span><span>{result.paymentProvider}</span></div>
              <div className="sumrow total"><span>Order total</span><span>{money(result.total)}</span></div>
            </div>
            <div className="panel-foot">
              <div className="row">
                <Link to="/" className="btn btn-secondary btn-sm">Continue shopping</Link>
                <Link to="/orders" className="btn btn-secondary btn-sm">Your orders</Link>
              </div>
            </div>
          </div>
        </aside>
      </div>
    </>
  )
}
