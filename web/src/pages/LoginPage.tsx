import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { useCart } from '../cart/CartContext'
import { api } from '../api/client'
import { GoogleButton } from '../components/GoogleButton'
import type { CartView } from '../api/types'

export function LoginPage() {
  const { login, completeTwoFactor, loginWithGoogle } = useAuth()
  const { cart, refresh } = useCart()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [challenge, setChallenge] = useState<string | null>(null)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function mergeCartThenGo() {
    if (cart?.id) {
      try { await api<CartView>('/cart/merge', { method: 'POST', body: { guestCartId: cart.id } }) } catch { /* ignore */ }
      await refresh()
    }
    navigate('/store')
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      const res = await login(email, password)
      if (res.twoFactorRequired && res.challengeToken) {
        setChallenge(res.challengeToken)
      } else {
        await mergeCartThenGo()
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign in failed.')
    } finally {
      setBusy(false)
    }
  }

  async function submitCode(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      await completeTwoFactor(challenge!, code)
      await mergeCartThenGo()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Invalid code.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="authpage">
      <div className="authcard">
        {!challenge ? (
          <>
            <h1>Sign in</h1>
            <p className="sub">Use your WidgetWorks account to track orders and check out faster.</p>
            <form onSubmit={submit}>
              <label className="field">
                <span>Email address</span>
                <input type="email" required autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
              </label>
              <label className="field">
                <span>Password</span>
                <input type="password" required autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
              </label>
              {error && <p className="alert alert-err">{error}</p>}
              <button className="btn btn-primary btn-block btn-lg" disabled={busy}>
                {busy ? 'Signing in…' : 'Sign in'}
              </button>
            </form>
          </>
        ) : (
          <>
            <h1>Two-step verification</h1>
            <p className="sub">Enter the 6-digit code from your authenticator app.</p>
            <form onSubmit={submitCode}>
              <label className="field">
                <span>Verification code</span>
                <input
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  maxLength={6}
                  required
                />
              </label>
              {error && <p className="alert alert-err">{error}</p>}
              <button className="btn btn-primary btn-block btn-lg" disabled={busy}>
                {busy ? 'Verifying…' : 'Verify'}
              </button>
            </form>
          </>
        )}

        <div className="divider">or</div>
        <div className="google-slot">
          <GoogleButton onCredential={async (idToken) => {
            try { await loginWithGoogle(idToken); await mergeCartThenGo() }
            catch (err) { setError(err instanceof Error ? err.message : 'Google sign-in failed.') }
          }} />
        </div>

        <p className="help" style={{ marginTop: 14, textAlign: 'center' }}>
          <Link to="/forgot-password" className="link">Forgot your password?</Link>
        </p>
      </div>

      <div className="auth-alt">
        New to WidgetWorks? <Link to="/register">Create an account</Link>
      </div>
    </div>
  )
}
