import { Link, useLocation } from 'react-router-dom'
import type { CheckoutResult } from '../api/types'
import { money } from '../lib/format'

export function OrderConfirmationPage() {
  const location = useLocation()
  const result = location.state as CheckoutResult | null

  if (!result) {
    return <section><h1>Thank you</h1><p className="muted">No recent order to show. <Link to="/">Back to shop →</Link></p></section>
  }

  return (
    <section>
      <h1>Order confirmed 🎉</h1>
      <p>Your order <strong>{result.orderNumber}</strong> is <strong>{result.status}</strong>.</p>
      <p>Total charged: {money(result.total)} via {result.paymentProvider}.</p>
      <p className="muted">A confirmation email is on its way. You can look up your order by number and email, or sign in to track it.</p>
      <Link to="/">Continue shopping →</Link>
    </section>
  )
}
