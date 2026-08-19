import { useState } from 'react'
import { Link } from 'react-router-dom'

/**
 * Landing page for first-time visitors. A working storefront is confusing without context:
 * people need to know it is a demo, that nothing will charge them, which accounts to use, and
 * where the emails go. Everything here is public, documented information — the demo credentials
 * are the repository's one sanctioned exception and are already in its README.
 */

const ACCOUNTS = [
  {
    role: 'Customer',
    email: 'demo@widgetworks.demo',
    password: 'DemoUser!Change01',
    summary: 'The everyday shopper.',
    can: ['Browse and search the catalog', 'Add to cart and check out', 'See their own order history and tracking'],
    cannot: ['Anything in the Admin area'],
  },
  {
    role: 'Manager',
    email: 'manager@widgetworks.demo',
    password: 'DemoManager!Change01',
    summary: 'Runs the shop day to day.',
    can: [
      'Everything a Customer can',
      'Admin → Catalog: add a widget, edit it, adjust stock, hide it from the storefront',
      'Admin → Orders: mark shipped or delivered, add tracking, cancel',
    ],
    cannot: ['Delete or retire a widget — that is Administrator-only', 'Manage users'],
  },
  {
    role: 'Administrator',
    email: 'admin@widgetworks.demo',
    password: 'DemoAdmin!Change01',
    summary: 'Full control.',
    can: [
      'Everything a Manager can',
      'Delete a widget — removed outright if never ordered, archived if it appears on an order',
      'Revoke another user’s sessions',
    ],
    cannot: [],
  },
]

function CopyField({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1600)
    } catch {
      /* clipboard blocked — the value is on screen to type instead */
    }
  }

  return (
    <div className="copyfield">
      <span className="copyfield-label">{label}</span>
      <code className="copyfield-value">{value}</code>
      <button type="button" className="btn btn-secondary btn-sm" onClick={copy}>
        {copied ? '✓ Copied' : 'Copy'}
      </button>
    </div>
  )
}

export function DemoGuidePage() {
  return (
    <div className="guide">
      <section className="guide-hero">
        <span className="hero-eyebrow">Portfolio demo</span>
        <h1>A widget store you can actually use.</h1>
        <p>
          Browse the catalog, fill a cart, and place a real order through a real checkout — with
          stock reservation, tax and shipping calculated server-side, and an order you can track
          afterwards. Everything works. Nothing is real.
        </p>
        <div className="row">
          <Link to="/store" className="btn btn-primary btn-lg">Enter the store</Link>
          <Link to="/login" className="btn btn-secondary btn-lg">Sign in with a demo account</Link>
        </div>
      </section>

      {/* The trust question, answered before anything else is asked of the visitor. */}
      <section className="panel guide-assure">
        <div className="panel-body">
          <h2><span aria-hidden="true">🔒</span> No payment is ever taken</h2>
          <p>
            Checkout runs against a <strong>mock payment gateway</strong>. No card details are
            collected, no payment processor is contacted, and no charge of any kind can occur —
            there is nothing to charge, because the form never asks for a card number.
          </p>
          <p className="muted small">
            The same code can run against Stripe, and when it does it is restricted to Stripe's
            <strong> test mode</strong>, which by design cannot bill a real card. Live keys are
            blocked from the repository by an automated secret scan.
          </p>
        </div>
      </section>

      <section>
        <div className="sec"><h2>Three roles, three accounts</h2></div>
        <p className="muted" style={{ marginBottom: 14 }}>
          You can browse and check out as a guest. Sign in to keep an order history — or to see
          how far each role is allowed to go. Every account below is already seeded.
        </p>
        <div className="guide-accounts">
          {ACCOUNTS.map((a) => (
            <div key={a.role} className="panel guide-role">
              <div className="panel-head">
                <h3>{a.role}</h3>
                <p className="muted small">{a.summary}</p>
              </div>
              <div className="panel-body">
                <CopyField label="Email" value={a.email} />
                <CopyField label="Password" value={a.password} />

                <ul className="guide-can">
                  {a.can.map((c) => <li key={c}><span aria-hidden="true">✓</span>{c}</li>)}
                  {a.cannot.map((c) => <li key={c} className="no"><span aria-hidden="true">✕</span>{c}</li>)}
                </ul>
              </div>
            </div>
          ))}
        </div>
        <p className="help" style={{ marginTop: 12 }}>
          These are throwaway accounts on a throwaway database. Please don&apos;t enter a real
          password anywhere on this site.
        </p>
      </section>

      <section>
        <div className="sec"><h2>Things worth trying</h2></div>
        <ol className="guide-steps">
          <li>
            <strong>Buy something.</strong> Add a widget, open the cart, and check out. Stock is
            reserved the moment the order is placed, so the catalog count moves.
          </li>
          <li>
            <strong>Pick a different payment method.</strong> Card and Google Pay settle
            immediately. <em>Klarna — Pay later</em> parks the order as <em>Awaiting payment</em>
            until the provider confirms it, and the confirmation page lets you play the provider
            and approve or decline it. <em>Test: declined card</em> always fails, so you can see
            the order cancel itself and release the stock.
          </li>
          <li>
            <strong>Sign in as the administrator</strong> and open Admin → Catalog. Adjust stock,
            hide a product, or delete one. A widget that has never been ordered is deleted; one
            that appears on an order is archived instead, so past orders still report correctly.
          </li>
        </ol>
      </section>

      <section className="panel">
        <div className="panel-body">
          <h2><span aria-hidden="true">✉️</span> About the emails</h2>
          <p>
            Placing an order, registering, and requesting a password reset all send real
            transactional email — a receipt with your line items, a welcome note, a reset link.
          </p>
          <p>
            On this hosted demo they are <strong>written to the application log rather than
            delivered</strong>, so nothing reaches a real inbox and you can use any address you
            like. You are not missing anything: sign in and open{' '}
            <Link to="/orders" className="link">Your orders</Link> — the order detail page shows
            the same line items, totals, payment method and tracking that the receipt contains.
          </p>
          <p className="muted small">
            Running the project locally with Docker swaps the log for a real inbox: it includes a
            mail catcher at <code>localhost:8025</code> where every message appears, HTML and all.
          </p>
        </div>
      </section>

      <section className="panel guide-tech">
        <div className="panel-body">
          <h2>What this is</h2>
          <p>
            A portfolio build of an end-to-end storefront: .NET 10 minimal API, Dapper and
            PostgreSQL, React and TypeScript, onion architecture. It covers the parts most demos
            skip — JWT auth with rotating refresh tokens, TOTP two-factor, Google sign-in, atomic
            stock reservation, server-side re-priced checkout, asynchronous payment confirmation
            by webhook, and the full order lifecycle.
          </p>
          <div className="row" style={{ marginTop: 12 }}>
            <a className="btn btn-secondary" href="https://github.com/bgard68/WidgetWorks" target="_blank" rel="noreferrer">
              Source on GitHub
            </a>
            <a className="btn btn-secondary" href="https://github.com/bgard68/WidgetWorks/tree/main/docs/handbook" target="_blank" rel="noreferrer">
              Engineering handbook
            </a>
            <Link to="/store" className="btn btn-primary">Enter the store</Link>
          </div>
        </div>
      </section>
    </div>
  )
}
