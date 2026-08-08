import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'

export function ResetPasswordPage() {
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    try {
      await api('/auth/reset-password', { method: 'POST', body: { token, newPassword: password } })
      setDone(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Reset failed.')
    }
  }

  return (
    <section className="narrow">
      <h1>Choose a new password</h1>
      {done ? (
        <p className="ok">Your password has been reset. <Link to="/login">Sign in →</Link></p>
      ) : (
        <form onSubmit={submit} className="form">
          <label>New password (min 8 chars)<input type="password" required minLength={8} value={password} onChange={(e) => setPassword(e.target.value)} /></label>
          {error && <p className="error">{error}</p>}
          <button disabled={!token}>Reset password</button>
          {!token && <p className="error">Missing reset token in the link.</p>}
        </form>
      )}
    </section>
  )
}
