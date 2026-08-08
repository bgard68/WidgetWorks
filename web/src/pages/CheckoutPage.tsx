import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { CheckoutResult, OrderQuote } from '../api/types'
import { useCart } from '../cart/CartContext'
import { money } from '../lib/format'

const STATES = ['AL','AK','AZ','AR','CA','CO','CT','DE','DC','FL','GA','HI','ID','IL','IN','IA','KS','KY','LA','ME','MD','MA','MI','MN','MS','MO','MT','NE','NV','NH','NJ','NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT','VA','WA','WV','WI','WY']

// Each method maps to a demo payment token the mock gateway understands. Card / Google Pay
// settle immediately (Paid); Klarna authorizes asynchronously (AwaitingPayment) and is
// confirmed by a provider webhook — see the order confirmation page.
interface PayMethod { id: string; label: string; sublabel: string; token: string; icon: string }

const PAY_METHODS: PayMethod[] = [
  { id: 'card', label: 'Credit / debit card', sublabel: 'Visa, Mastercard, Amex — charged now', token: 'tok_visa_ok', icon: '💳' },
  { id: 'googlepay', label: 'Google Pay', sublabel: 'Pay with a saved card — charged now', token: 'gpay_demo', icon: '📱' },
  { id: 'klarna', label: 'Klarna — Pay later', sublabel: 'Pay in 4. Confirmed by the provider after checkout', token: 'klarna_demo', icon: '🛍️' },
  { id: 'decline', label: 'Test: declined card', sublabel: 'Always declines — to demo the failure path', token: 'card-decline', icon: '⛔' },
]

export function CheckoutPage() {
  const { cart, clearLocal } = useCart()
  const navigate = useNavigate()
  const [form, setForm] = useState({
    email: '', name: '', line1: '', line2: '', city: '', state: 'CA', postalCode: '',
    shippingMethod: 'Standard',
  })
  const [method, setMethod] = useState('card')
  const [quote, setQuote] = useState<OrderQuote | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const cartId = cart?.id

  useEffect(() => {
    if (!cartId) return
    api<OrderQuote>('/checkout/quote', {
      method: 'POST',
      body: { cartId, stateCode: form.state, shippingMethod: form.shippingMethod },
    }).then(setQuote).catch((e) => setError(e.message))
  }, [cartId, form.state, form.shippingMethod])

  if (!cart || cart.items.length === 0) {
    return <section><h1>Checkout</h1><p className="muted">Your cart is empty.</p></section>
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setForm({ ...form, [k]: e.target.value })

  async function placeOrder(e: React.FormEvent) {
    e.preventDefault()
    if (!cartId) return
    const paymentToken = PAY_METHODS.find((m) => m.id === method)?.token ?? 'tok_visa_ok'
    setBusy(true)
    setError(null)
    try {
      const result = await api<CheckoutResult>('/checkout', {
        method: 'POST',
        body: {
          cartId,
          email: form.email,
          name: form.name,
          line1: form.line1,
          line2: form.line2 || null,
          city: form.city,
          state: form.state,
          postalCode: form.postalCode,
          country: 'US',
          shippingMethod: form.shippingMethod,
          paymentToken,
        },
      })
      clearLocal()
      navigate(`/order-confirmation`, { state: { ...result, email: form.email } })
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Checkout failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="checkout">
      <h1>Checkout</h1>
      <form onSubmit={placeOrder} className="form">
        <label>Email<input type="email" required value={form.email} onChange={set('email')} /></label>
        <label>Full name<input required value={form.name} onChange={set('name')} /></label>
        <label>Address line 1<input required value={form.line1} onChange={set('line1')} /></label>
        <label>Address line 2<input value={form.line2} onChange={set('line2')} /></label>
        <div className="row">
          <label>City<input required value={form.city} onChange={set('city')} /></label>
          <label>State
            <select value={form.state} onChange={set('state')}>
              {STATES.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </label>
          <label>ZIP<input required value={form.postalCode} onChange={set('postalCode')} /></label>
        </div>
        <label>Shipping
          <select value={form.shippingMethod} onChange={set('shippingMethod')}>
            <option value="Standard">Standard</option>
            <option value="Express">Express</option>
          </select>
        </label>

        <div className="paylabel">Payment method</div>
        <div className="paymethods">
          {PAY_METHODS.map((m) => (
            <label key={m.id} className={`paymethod${method === m.id ? ' on' : ''}`}>
              <span className="pm-ico" aria-hidden="true">{m.icon}</span>
              <span className="pm-text">
                <span className="pm-main">{m.label}</span>
                <span className="pm-sub">{m.sublabel}</span>
              </span>
              <input type="radio" name="paymethod" checked={method === m.id} onChange={() => setMethod(m.id)} />
            </label>
          ))}
        </div>
        <p className="muted small">Demo gateway — no real charge. Klarna demonstrates an asynchronous
          “pay later” authorization that a webhook confirms; card and Google Pay settle immediately.</p>

        {error && <p className="error">{error}</p>}
        <button disabled={busy}>{busy ? 'Placing order…' : 'Place order'}</button>
      </form>
      {quote && (
        <aside className="summary">
          <h2>Order summary</h2>
          <div>Subtotal <span>{money(quote.subtotal)}</span></div>
          <div>Shipping ({quote.shippingMethod}) <span>{money(quote.shipping)}</span></div>
          <div>Tax ({(quote.taxRate * 100).toFixed(2)}% {quote.stateCode}) <span>{money(quote.tax)}</span></div>
          <div className="total">Total <span>{money(quote.total)}</span></div>
        </aside>
      )}
    </section>
  )
}
