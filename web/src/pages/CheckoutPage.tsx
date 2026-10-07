import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError, api } from '../api/client'
import type { CheckoutResult, OrderQuote } from '../api/types'
import { useCart } from '../cart/CartContext'
import { money } from '../lib/format'
import { ProductImage } from '../components/ProductImage'

const STATES = ['AL','AK','AZ','AR','CA','CO','CT','DE','DC','FL','GA','HI','ID','IL','IN','IA','KS','KY','LA','ME','MD','MA','MI','MN','MS','MO','MT','NE','NV','NH','NJ','NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT','VA','WA','WV','WI','WY']

const SHIPPING = [
  { id: 'Standard', label: 'Standard shipping', sub: 'Free on orders over $75' },
  { id: 'Express', label: 'Express shipping', sub: 'Prioritised handling and dispatch' },
]

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

const FORM_ID = 'checkout-form'

/**
 * The server answers 409 `checkout_in_flight` when an earlier copy of this exact request is still
 * being processed — two tabs, a proxy that retried, a click that beat the disabled button. The order
 * is being placed; the only sensible thing a client can do is wait and ask again under the same key,
 * which either replays the order or finds it still running.
 *
 * Bounded on purpose, and short: these waits happen while a shopper is watching a spinner. Four
 * attempts over ~1.5s covers a checkout slowed by a sleepy database, and anything longer is better
 * spent telling them than hiding from them. Permanent conflicts (a key reused for different
 * content) are never retried — no amount of waiting changes those.
 */
const IN_FLIGHT_RETRY_DELAYS_MS = [150, 400, 1000]

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

  // One key per checkout attempt, so the server can tell a retry from a second order.
  //
  // A ref rather than state on purpose: it must survive re-renders without causing one, and a
  // second click that slips past the busy flag has to reuse the same value — minting a fresh key
  // there would hand the server a brand-new request and place the duplicate order this is meant to
  // prevent. Cleared only when the server refuses the attempt on its merits (see placeOrder), which
  // is the one case where the next click really is a different request.
  const idempotencyKey = useRef<string | null>(null)

  useEffect(() => {
    if (!cartId) return
    api<OrderQuote>('/checkout/quote', {
      method: 'POST',
      body: { cartId, stateCode: form.state, shippingMethod: form.shippingMethod },
    }).then(setQuote).catch((e) => setError(e.message))
  }, [cartId, form.state, form.shippingMethod])

  if (!cart || cart.items.length === 0) {
    return (
      <div className="empty">
        <span className="empty-ico" aria-hidden="true">🧾</span>
        <h2>There is nothing to check out</h2>
        <p>Your cart is empty — add a widget or two and come back.</p>
        <Link to="/store" className="btn btn-primary">Browse widgets</Link>
      </div>
    )
  }

  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setForm({ ...form, [k]: e.target.value })

  async function placeOrder(e: React.FormEvent) {
    e.preventDefault()
    /* v8 ignore next -- an empty cart returns above, so a cart id always exists by here */
    if (!cartId) return
    /* v8 ignore next -- method is only ever set from PAY_METHODS, so the lookup always matches */
    const paymentToken = PAY_METHODS.find((m) => m.id === method)?.token ?? 'tok_visa_ok'
    setBusy(true)
    setError(null)
    try {
      idempotencyKey.current ??= crypto.randomUUID()
      const key = idempotencyKey.current

      const submit = () => api<CheckoutResult>('/checkout', {
        method: 'POST',
        headers: { 'Idempotency-Key': key },
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

      let result: CheckoutResult
      for (let attempt = 0; ; attempt++) {
        try {
          result = await submit()
          break
        } catch (err) {
          const waitAndRetry = err instanceof ApiError
            && err.status === 409
            && err.code === 'checkout_in_flight'
            && attempt < IN_FLIGHT_RETRY_DELAYS_MS.length
          if (!waitAndRetry) throw err
          await new Promise((resolve) => setTimeout(resolve, IN_FLIGHT_RETRY_DELAYS_MS[attempt]))
        }
      }

      clearLocal()
      navigate('/order-confirmation', { state: { ...result, email: form.email } })
    } catch (err) {
      // A 400 is the server refusing this attempt — a declined card, an address it would not take.
      // Whatever the shopper changes next is genuinely a different request, so the key is retired;
      // keeping it would answer the corrected attempt with the original refusal.
      //
      // Everything else (a dropped connection, a timeout, a 5xx) leaves the outcome unknown, and
      // the key is exactly what makes trying again safe: it either replays the order that was
      // placed or is told the first attempt is still running.
      if (err instanceof ApiError && err.status === 400) {
        idempotencyKey.current = null
      }

      setError(err instanceof Error ? err.message : 'Checkout failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="co-head">
        <nav className="crumbs" aria-label="Breadcrumb">
          <Link to="/cart" className="link">Cart</Link>
          <span className="sep" aria-hidden="true">›</span>
          <span className="cur">Checkout</span>
        </nav>
        <h1>🔒 Secure checkout</h1>
      </div>

      <div className="co">
        <form id={FORM_ID} className="co-main" onSubmit={placeOrder}>
          {/* 1 — contact ------------------------------------------------ */}
          <section className="step">
            <div className="step-head">
              <span className="step-num" aria-hidden="true">1</span>
              <h2>Contact details</h2>
            </div>
            <div className="step-body">
              <div className="grid-2">
                <label className="field">
                  <span>Email address</span>
                  <input type="email" required autoComplete="email" value={form.email} onChange={set('email')} />
                </label>
                <label className="field">
                  <span>Full name</span>
                  <input required autoComplete="name" value={form.name} onChange={set('name')} />
                </label>
              </div>
              <p className="help">We&apos;ll send the order confirmation and tracking updates here.</p>
            </div>
          </section>

          {/* 2 — shipping ---------------------------------------------- */}
          <section className="step">
            <div className="step-head">
              <span className="step-num" aria-hidden="true">2</span>
              <h2>Shipping address</h2>
            </div>
            <div className="step-body">
              <label className="field">
                <span>Address line 1</span>
                <input required autoComplete="address-line1" value={form.line1} onChange={set('line1')} />
              </label>
              <label className="field">
                <span>Address line 2 <span className="muted">(optional)</span></span>
                <input autoComplete="address-line2" value={form.line2} onChange={set('line2')} />
              </label>
              <div className="grid-3">
                <label className="field">
                  <span>City</span>
                  <input required autoComplete="address-level2" value={form.city} onChange={set('city')} />
                </label>
                <label className="field">
                  <span>State</span>
                  <select value={form.state} onChange={set('state')} autoComplete="address-level1">
                    {STATES.map((s) => <option key={s} value={s}>{s}</option>)}
                  </select>
                </label>
                <label className="field">
                  <span>ZIP code</span>
                  <input required autoComplete="postal-code" inputMode="numeric" value={form.postalCode} onChange={set('postalCode')} />
                </label>
              </div>

              <div className="field">
                <span className="field-label">Delivery speed</span>
                <div className="optionlist">
                  {SHIPPING.map((s) => (
                    <label key={s.id} className={`optioncard${form.shippingMethod === s.id ? ' on' : ''}`}>
                      <span className="pm-ico" aria-hidden="true">{s.id === 'Express' ? '⚡' : '📦'}</span>
                      <span className="pm-text">
                        <span className="pm-main">{s.label}</span>
                        <span className="pm-sub">{s.sub}</span>
                      </span>
                      <input
                        type="radio"
                        name="shipping"
                        checked={form.shippingMethod === s.id}
                        onChange={() => setForm({ ...form, shippingMethod: s.id })}
                      />
                    </label>
                  ))}
                </div>
              </div>
            </div>
          </section>

          {/* 3 — payment ------------------------------------------------ */}
          <section className="step">
            <div className="step-head">
              <span className="step-num" aria-hidden="true">3</span>
              <h2>Payment method</h2>
            </div>
            <div className="step-body">
              <div className="optionlist">
                {PAY_METHODS.map((m) => (
                  <label key={m.id} className={`optioncard${method === m.id ? ' on' : ''}`}>
                    <span className="pm-ico" aria-hidden="true">{m.icon}</span>
                    <span className="pm-text">
                      <span className="pm-main">{m.label}</span>
                      <span className="pm-sub">{m.sublabel}</span>
                    </span>
                    <input type="radio" name="paymethod" checked={method === m.id} onChange={() => setMethod(m.id)} />
                  </label>
                ))}
              </div>
              <p className="alert alert-info">
                Demo gateway — no real charge. Klarna demonstrates an asynchronous “pay later”
                authorization that a webhook confirms; card and Google Pay settle immediately.
              </p>
            </div>
          </section>

          {error && <p className="alert alert-err">{error}</p>}

          <button className="btn btn-buy btn-lg btn-block" disabled={busy}>
            {busy ? 'Placing order…' : 'Place your order'}
          </button>
        </form>

        {/* Order summary ------------------------------------------------ */}
        <aside className="summary">
          <div className="panel">
            <div className="panel-head"><h2>Order summary</h2></div>
            <div className="panel-body">
              <button className="btn btn-buy btn-block btn-lg" form={FORM_ID} disabled={busy}>
                {busy ? 'Placing order…' : 'Place your order'}
              </button>
              <p className="help" style={{ margin: '10px 0 14px' }}>
                By placing your order you agree to this demo store&apos;s terms.
              </p>

              {quote ? (
                <>
                  <div className="sumrow">
                    <span>Items ({quote.itemCount})</span><span>{money(quote.subtotal)}</span>
                  </div>
                  <div className={`sumrow${quote.shipping === 0 ? ' free' : ''}`}>
                    <span>Shipping ({quote.shippingMethod})</span>
                    <span>{quote.shipping === 0 ? 'FREE' : money(quote.shipping)}</span>
                  </div>
                  <div className="sumrow">
                    <span>Tax ({(quote.taxRate * 100).toFixed(2)}% {quote.stateCode})</span>
                    <span>{money(quote.tax)}</span>
                  </div>
                  <div className="sumrow total">
                    <span>Order total</span>
                    <span>{money(quote.total)}</span>
                  </div>
                </>
              ) : (
                <>
                  <div className="sk sk-line" />
                  <div className="sk sk-line w60" />
                </>
              )}

              <div className="summary-items">
                {cart.items.map((line) => (
                  <div key={line.widgetId} className="summary-item">
                    <span className="thumb">
                      <ProductImage sku={line.sku} />
                    </span>
                    <span className="nm">{line.name} <span className="muted">× {line.quantity}</span></span>
                    <span className="amt">{money(line.lineSubtotal)}</span>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </aside>
      </div>
    </>
  )
}
