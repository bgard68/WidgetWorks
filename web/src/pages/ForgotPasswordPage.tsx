import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      await api('/auth/forgot-password', { method: 'POST', body: { email } })
    } catch (err) {
      // Deliberately not surfaced: showing this would tell a stranger which
      // addresses have accounts. Logged so a real outage is still diagnosable.
      console.warn('Password reset request failed; the response stays identical.', err)
    }
    setBusy(false)
    setSent(true)
  }

  return (
    <div className="authpage">
      <div className="authcard">
        <h1>Reset your password</h1>
        {sent ? (
          <>
            <p className="sub">Check your inbox.</p>
            <p className="alert alert-ok">
              If that email has an account, a reset link is on its way. In dev mode the link is
              written to the application log.
            </p>
          </>
        ) : (
          <>
            <p className="sub">Enter your email and we&apos;ll send you a reset link.</p>
            <form onSubmit={submit}>
              <label className="field">
                <span>Email address</span>
                <input type="email" required autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
              </label>
              <button className="btn btn-primary btn-block btn-lg" disabled={busy}>
                {busy ? 'Sending…' : 'Send reset link'}
              </button>
            </form>
          </>
        )}
      </div>

      <div className="auth-alt">
        Remembered it? <Link to="/login">Back to sign in</Link>
      </div>
    </div>
  )
}
