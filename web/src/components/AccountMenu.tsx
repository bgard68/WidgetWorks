import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { api } from '../api/client'
import type { ProfileView } from '../api/types'

/**
 * The account slot in the header, and the menu behind it.
 *
 * Replaces a plain link to /login. The link was the reason there was no way into an account area at
 * all once you were signed in: the slot stopped being about your account and became a shortcut to
 * orders.
 *
 * Shaped after two large retailers — Company A, a general marketplace, and Company B, a big-box
 * chain — both of which do the same three things:
 *
 * - **One entry point.** Sign in and create account are never siblings. The menu leads with a Sign in
 *   button and demotes "create an account" to a line of small print underneath.
 * - **The slot keeps its identity.** "Hello, sign in" becomes "Hello, Jane" in the same position with
 *   the same shape, rather than turning into a different control.
 * - **Short.** Company B lists four things. Company A lists eighteen because it runs eighteen
 *   businesses; copying that here would be a menu of mostly empty promises.
 *
 * Opens on click rather than hover: hover menus are unreachable by keyboard and hostile on touch,
 * and the behaviour is identical for a mouse user who clicks.
 */
export function AccountMenu() {
  const { isAuthenticated, logout } = useAuth()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [displayName, setDisplayName] = useState<string | null>(null)
  const wrap = useRef<HTMLDivElement>(null)

  // Fetched here rather than threaded through AuthContext. Putting it in the context would fire a
  // profile request for every visitor on every page — a guaranteed 401 for guests — and would make
  // the provider something every test has to stub. This one only runs when there is a session.
  useEffect(() => {
    if (!isAuthenticated) {
      setDisplayName(null)
      return
    }

    let live = true
    api<ProfileView>('/account/profile')
      .then((p) => { if (live) setDisplayName(p.displayName) })
      .catch(() => { /* The greeting falls back to "there"; a name is not worth an error banner. */ })
    return () => { live = false }
  }, [isAuthenticated])

  // Close on a click anywhere else, and on Escape. Both are expected of a menu, and their absence is
  // the thing that makes a hand-rolled dropdown feel broken.
  useEffect(() => {
    if (!open) return

    const onPointerDown = (e: MouseEvent) => {
      if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false)
    }
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }

    document.addEventListener('mousedown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  const greeting = isAuthenticated
    ? `Hello, ${displayName?.trim() || 'there'}`
    : 'Hello, sign in'

  function signOut() {
    setOpen(false)
    logout()
    navigate('/store')
  }

  return (
    <div className="acctmenu" ref={wrap}>
      <button
        type="button"
        className="hdr-btn acctmenu-trigger"
        aria-expanded={open}
        aria-haspopup="true"
        onClick={() => setOpen(!open)}
      >
        <span className="l1">{greeting}</span>
        <span className="l2">Account <span aria-hidden="true">▾</span></span>
      </button>

      {open && (
        <div className="acctmenu-pop" role="menu">
          {isAuthenticated ? (
            <>
              {/* Orders first. The job someone came to do outranks the settings — one retailer puts
                  purchase history above account, another puts paying a bill above account overview. */}
              <Link to="/orders" className="acctmenu-item" role="menuitem" onClick={() => setOpen(false)}>Your orders</Link>
              <Link to="/account" className="acctmenu-item" role="menuitem" onClick={() => setOpen(false)}>Account home</Link>
              <Link to="/account/security" className="acctmenu-item" role="menuitem" onClick={() => setOpen(false)}>Login and security</Link>
              <div className="acctmenu-sep" />
              <button type="button" className="acctmenu-item" role="menuitem" onClick={signOut}>Sign out</button>
            </>
          ) : (
            <>
              <Link to="/login" className="btn btn-primary btn-block btn-sm" onClick={() => setOpen(false)}>Sign in</Link>
              {/* Small print, not a sibling button. None of the three retailers examined present
                  "sign in" and "create account" as equal choices — the one you want is overwhelmingly
                  the first, and offering both equally just makes people stop and decide. */}
              <p className="acctmenu-new">
                New to WidgetWorks? <Link to="/register" onClick={() => setOpen(false)}>Create an account</Link>
              </p>
              <div className="acctmenu-sep" />
              {/* Guest checkout exists, so guest order tracking has to as well — otherwise buying
                  without an account means never seeing the order again. */}
              <Link to="/track-order" className="acctmenu-item" role="menuitem" onClick={() => setOpen(false)}>Track an order</Link>
            </>
          )}
        </div>
      )}
    </div>
  )
}
