import { useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { api } from '../api/client'
import type { CheckoutResult } from '../api/types'
import { money } from '../lib/format'

type ConfirmState = CheckoutResult & { email?: string }

export function OrderConfirmationPage() {
  const location = useLocation()
  const result = location.state as ConfirmState | null
  const [status, setStatus] = useState(result?.status ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (!result) {
    return (
      <section className="narrow">
        <h1>Thank you</h1>
        <p className="muted">No recent order to show. <Link to="/">Back to shop →</Link></p>
      </section>
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

  const heading = paid ? 'Order confirmed 🎉' : failed ? 'Payment not completed' : 'Almost there…'

  return (
    <section className="narrow">
      <h1>{heading}</h1>
      <p>Your order <strong>{result.orderNumber}</strong> is <strong>{status}</strong>.</p>

      {paid && (
        <>
          <p>Total charged: {money(result.total)} via {result.paymentProvider}.</p>
          <p className="muted">A confirmation email is on its way. You can look up your order by number and email, or sign in to track it.</p>
        </>
      )}

      {awaiting && (
        <>
          <p className="muted">
            {result.paymentProvider} is processing your payment of {money(result.total)}. In a live store you’d be
            redirected to the provider to approve it; we’ll email you once it settles. Your items are reserved until then.
          </p>
          <div className="paypending">
            <p className="small muted">Demo — simulate the provider’s webhook callback:</p>
            <div className="row">
              <button disabled={busy} onClick={() => simulate('succeeded')}>Approve payment</button>
              <button type="button" className="linkbtn" disabled={busy} onClick={() => simulate('failed')}>Simulate decline</button>
            </div>
          </div>
        </>
      )}

      {failed && (
        <p className="muted">The payment wasn’t completed, so the order was cancelled and your items released.
          You can head back to the shop and try again.</p>
      )}

      {error && <p className="error">{error}</p>}
      <Link to="/">Continue shopping →</Link>
    </section>
  )
}
