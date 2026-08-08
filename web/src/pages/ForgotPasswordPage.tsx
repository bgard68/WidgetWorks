import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    try { await api('/auth/forgot-password', { method: 'POST', body: { email } }) } catch { /* ignore */ }
    setSent(true)
  }

  return (
    <section className="narrow">
      <h1>Reset your password</h1>
      {sent ? (
        <p className="ok">If that email has an account, a reset link is on its way. Check the app log in dev mode.</p>
      ) : (
        <form onSubmit={submit} className="form">
          <label>Email<input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <button>Send reset link</button>
        </form>
      )}
      <p className="muted"><Link to="/login">Back to sign in</Link></p>
    </section>
  )
}
