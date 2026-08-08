import { Link, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { useCart } from '../cart/CartContext'

export function Layout() {
  const { isAuthenticated, isStaff, logout } = useAuth()
  const { itemCount } = useCart()

  return (
    <div className="app">
      <header className="nav">
        <Link to="/" className="brand">WidgetWorks</Link>
        <nav>
          <Link to="/">Shop</Link>
          <Link to="/cart">Cart{itemCount > 0 ? ` (${itemCount})` : ''}</Link>
          {isAuthenticated && <Link to="/orders">My Orders</Link>}
          {isStaff && <Link to="/admin/widgets">Admin</Link>}
          {isAuthenticated
            ? <button className="linkbtn" onClick={logout}>Sign out</button>
            : <Link to="/login">Sign in</Link>}
        </nav>
      </header>
      <main className="content">
        <Outlet />
      </main>
      <footer className="foot">WidgetWorks — a portfolio demo store.</footer>
    </div>
  )
}
