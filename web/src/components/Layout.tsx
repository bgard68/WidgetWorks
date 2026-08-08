import { Link, Outlet, useNavigate, useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { useCart } from '../cart/CartContext'

const CATEGORIES: { label: string; q: string }[] = [
  { label: 'All', q: '' },
  { label: 'Standard', q: 'standard' },
  { label: 'Deluxe', q: 'deluxe' },
  { label: 'Mega', q: 'mega' },
  { label: 'Mini', q: 'mini' },
  { label: 'Kits', q: 'kit' },
]

export function Layout() {
  const { isAuthenticated, isStaff, logout } = useAuth()
  const { itemCount } = useCart()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const q = params.get('q') ?? ''

  const setSearch = (value: string) =>
    navigate(value ? `/?q=${encodeURIComponent(value)}` : '/', { replace: true })

  return (
    <div className="app">
      <div className="util">
        <div className="util-wrap">
          <span>🚚 Free standard shipping on orders over $75 · Demo store</span>
          <span>
            {isAuthenticated
              ? <a onClick={logout}>Sign out</a>
              : <><Link to="/login">Sign in</Link> · <Link to="/register">Create account</Link></>}
          </span>
        </div>
      </div>

      <header className="nav">
        <Link to="/" className="brand"><span className="bolt">⚡</span> Widget<b>Works</b></Link>
        <div className="searchbar">
          <input
            value={q}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search widgets, kits, accessories…"
          />
          <button onClick={() => navigate(q ? `/?q=${encodeURIComponent(q)}` : '/')}>Search</button>
        </div>
        <nav className="navlinks">
          {isAuthenticated && <Link to="/orders">My Orders</Link>}
          {isStaff && <Link to="/admin/widgets">Admin</Link>}
          <Link to="/cart" className="cartbtn">🛒 Cart{itemCount > 0 && <span className="count">{itemCount}</span>}</Link>
        </nav>
      </header>

      <div className="cats">
        <div className="cats-wrap">
          {CATEGORIES.map((c) => (
            <Link
              key={c.label}
              to={c.q ? `/?q=${c.q}` : '/'}
              className={(c.q === q || (c.q === '' && !q)) ? 'on' : ''}
            >
              {c.label}
            </Link>
          ))}
        </div>
      </div>

      <main className="content"><Outlet /></main>

      <footer className="foot">
        <div className="foot-wrap">
          <div className="brandcol">
            <h4>WidgetWorks</h4>
            <p>The dependable widget store — a portfolio demo of a production-shaped .NET + React build.</p>
          </div>
          <div><h4>Shop</h4><Link to="/">All widgets</Link><Link to="/?q=kit">Kits</Link><Link to="/?q=mega">Mega</Link></div>
          <div><h4>Account</h4><Link to="/login">Sign in</Link><Link to="/orders">My orders</Link></div>
          <div><h4>Help</h4><a>Shipping</a><a>Returns</a><a>About</a></div>
        </div>
        <div className="foot-bar">© 2026 WidgetWorks — demo store. Sample product photos.</div>
      </footer>
    </div>
  )
}
