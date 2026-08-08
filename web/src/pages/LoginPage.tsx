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

  async function mergeCartThenGo() {
    if (cart?.id) {
      try { await api<CartView>('/cart/merge', { method: 'POST', body: { guestCartId: cart.id } }) } catch { /* ignore */ }
      await refresh()
    }
    navigate('/')
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      const res = await login(email, password)
      if (res.twoFactorRequired && res.challengeToken) {
        setChallenge(res.challengeToken)
      } else {
        await mergeCartThenGo()
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign in failed.')
    }
  }

  async function submitCode(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      await completeTwoFactor(challenge!, code)
      await mergeCartThenGo()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Invalid code.')
    }
  }

  return (
    <section className="narrow">
      <h1>Sign in</h1>
      {!challenge ? (
        <form onSubmit={submit} className="form">
          <label>Email<input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <label>Password<input type="password" required value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          {error && <p className="error">{error}</p>}
          <button>Sign in</button>
        </form>
      ) : (
        <form onSubmit={submitCode} className="form">
          <p>Enter the 6-digit code from your authenticator app.</p>
          <label>Code<input value={code} onChange={(e) => setCode(e.target.value)} /></label>
          {error && <p className="error">{error}</p>}
          <button>Verify</button>
        </form>
      )}
      <div className="or">or</div>
      <GoogleButton onCredential={async (idToken) => {
        try { await loginWithGoogle(idToken); await mergeCartThenGo() }
        catch (err) { setError(err instanceof Error ? err.message : 'Google sign-in failed.') }
      }} />
      <p className="muted">
        <Link to="/register">Create an account</Link> · <Link to="/forgot-password">Forgot password?</Link>
      </p>
    </section>
  )
}
