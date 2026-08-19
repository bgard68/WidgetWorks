import { useEffect, useState } from 'react'
import { Link, Outlet, useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { useCart } from '../cart/CartContext'
import { CATEGORIES, FREE_SHIPPING_THRESHOLD } from '../lib/catalog'

export function Layout() {
  const { isAuthenticated, isStaff, logout } = useAuth()
  const { itemCount } = useCart()
  const navigate = useNavigate()
  const location = useLocation()
  const [params] = useSearchParams()

  const q = params.get('q') ?? ''
  const cat = params.get('cat') ?? ''
  const onCatalog = location.pathname === '/store'

  // The input is local so typing doesn't re-run the catalog query on every
  // keystroke; the URL (and the fetch) updates when the search is submitted.
  const [term, setTerm] = useState(q)
  useEffect(() => { setTerm(q) }, [q])

  const go = (nextQ: string, nextCat: string) => {
    const sp = new URLSearchParams()
    if (nextQ.trim()) sp.set('q', nextQ.trim())
    if (nextCat) sp.set('cat', nextCat)
    const qs = sp.toString()
    navigate(qs ? `/store?${qs}` : '/store')
  }

  return (
    <div className="app">
      {/* Utility strip ------------------------------------------------- */}
      <div className="util">
        <div className="wrap util-in">
          <span className="util-promo">
            <span aria-hidden="true">🚚</span>
            Free standard shipping on orders over ${FREE_SHIPPING_THRESHOLD} ·{' '}
            <Link to="/">Demo store — read the guide</Link>
          </span>
          <span className="util-links">
            <a href="https://github.com/bgard68/WidgetWorks" target="_blank" rel="noreferrer">
              About this build
            </a>
            {isAuthenticated
              ? <button type="button" className="btn-link" onClick={logout}>Sign out</button>
              : <Link to="/login">Sign in</Link>}
          </span>
        </div>
      </div>

      {/* Header --------------------------------------------------------- */}
      <header className="hdr">
        <div className="wrap hdr-in">
          <Link to="/store" className="brand" aria-label="WidgetWorks home">
            <span className="brand-mark" aria-hidden="true">⚡</span>
            <span>
              <span className="brand-name">Widget<b>Works</b></span>
              <span className="brand-tag">Dependable parts</span>
            </span>
          </Link>

          <Link to="/store?cat=kit" className="deliver">
            <span className="ico" aria-hidden="true">📍</span>
            <span className="lines">
              <span className="l1">Shipping to</span>
              <span className="l2">United States</span>
            </span>
          </Link>

          <form
            className="search"
            role="search"
            onSubmit={(e) => { e.preventDefault(); go(term, cat) }}
          >
            <label className="sr-only" htmlFor="search-scope">Search category</label>
            <select
              id="search-scope"
              className="search-scope"
              value={cat}
              onChange={(e) => go(term, e.target.value)}
            >
              {CATEGORIES.map((c) => (
                <option key={c.slug || 'all'} value={c.slug}>
                  {c.slug ? c.label : 'All'}
                </option>
              ))}
            </select>
            <label className="sr-only" htmlFor="search-input">Search widgets</label>
            <input
              id="search-input"
              className="search-input"
              type="search"
              value={term}
              onChange={(e) => setTerm(e.target.value)}
              placeholder="Search widgets, kits and accessories…"
            />
            <button className="search-go" type="submit" aria-label="Search">
              <span aria-hidden="true">🔍</span>
            </button>
          </form>

          <div className="hdr-actions">
            {isAuthenticated ? (
              <Link to="/orders" className="hdr-btn">
                <span className="l1">Returns</span>
                <span className="l2">&amp; Orders</span>
              </Link>
            ) : (
              <Link to="/login" className="hdr-btn">
                <span className="l1">Hello, sign in</span>
                <span className="l2">Account &amp; Lists</span>
              </Link>
            )}

            {isStaff && (
              <Link to="/admin/widgets" className="hdr-btn">
                <span className="l1">Staff</span>
                <span className="l2">Admin</span>
              </Link>
            )}

            <Link to="/cart" className="cart-btn" aria-label={`Cart, ${itemCount} item${itemCount === 1 ? '' : 's'}`}>
              <span className="cart-ico" aria-hidden="true">
                🛒
                {itemCount > 0 && <span className="cart-count">{itemCount}</span>}
              </span>
              <span className="cart-label">Cart</span>
            </Link>
          </div>
        </div>
      </header>

      {/* Category rail --------------------------------------------------- */}
      <nav className="rail" aria-label="Product categories">
        <div className="wrap rail-in">
          {CATEGORIES.map((c) => (
            <Link
              key={c.slug || 'all'}
              to={c.slug ? `/store?cat=${c.slug}` : '/store'}
              className={onCatalog && c.slug === cat ? 'on' : ''}
            >
              {c.slug ? c.label : 'All widgets'}
            </Link>
          ))}
          <Link to="/store?cat=kit" className="rail-deal">Today&apos;s deals</Link>
        </div>
      </nav>

      <main className="content"><Outlet /></main>

      {/* Footer ---------------------------------------------------------- */}
      <footer className="foot">
        <button
          type="button"
          className="foot-top"
          onClick={() => window.scrollTo({ top: 0, behavior: 'smooth' })}
        >
          Back to top
        </button>

        <div className="foot-cols">
          <div className="foot-col foot-brand">
            <span className="brand">
              <span className="brand-mark" aria-hidden="true">⚡</span>
              <span className="brand-name">Widget<b>Works</b></span>
            </span>
            <p>
              The dependable widget store — a portfolio demo of a production-shaped
              .NET&nbsp;+&nbsp;React build, from catalog and stock reservation through
              payments and the full order lifecycle.
            </p>
            <div className="foot-badges">
              <span className="foot-badge"><span aria-hidden="true">🔒</span> Secure checkout</span>
              <span className="foot-badge"><span aria-hidden="true">↩️</span> 30-day returns</span>
            </div>
          </div>

          <div className="foot-col">
            <h4>Shop</h4>
            <Link to="/store">All widgets</Link>
            <Link to="/store?cat=kit">Kits</Link>
            <Link to="/store?cat=mega">Mega widgets</Link>
            <Link to="/store?cat=mini">Mini widgets</Link>
          </div>

          <div className="foot-col">
            <h4>Your account</h4>
            <Link to="/login">Sign in</Link>
            <Link to="/register">Create account</Link>
            <Link to="/orders">Your orders</Link>
            <Link to="/cart">Your cart</Link>
          </div>

          <div className="foot-col">
            <h4>Customer service</h4>
            <Link to="/store?cat=">Shipping rates</Link>
            <Link to="/store?cat=">Returns policy</Link>
            <Link to="/forgot-password">Password help</Link>
          </div>

          <div className="foot-col">
            <h4>About the project</h4>
            <Link to="/">Demo guide</Link>
            <a href="https://github.com/bgard68/WidgetWorks" target="_blank" rel="noreferrer">Source on GitHub</a>
            <a href="https://github.com/bgard68/WidgetWorks/tree/main/docs/handbook" target="_blank" rel="noreferrer">Engineering handbook</a>
            <a href="https://github.com/bgard68/WidgetWorks/blob/main/SECURITY.md" target="_blank" rel="noreferrer">Security policy</a>
          </div>
        </div>

        <div className="foot-bar">
          © {new Date().getFullYear()} WidgetWorks
          <span className="sep">·</span>Demo store — no real orders are fulfilled
          <span className="sep">·</span>Sample product photography
        </div>
      </footer>
    </div>
  )
}
