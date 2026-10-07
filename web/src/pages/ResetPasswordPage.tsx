import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import { PasswordRequirements } from '../components/PasswordRequirements'
import { MIN_PASSWORD_LENGTH, meetsPasswordPolicy } from '../lib/passwordPolicy'

export function ResetPasswordPage() {
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      await api('/auth/reset-password', { method: 'POST', body: { token, newPassword: password } })
      setDone(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Reset failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="authpage">
      <div className="authcard">
        <h1>Choose a new password</h1>
        {done ? (
          <>
            <p className="alert alert-ok">Your password has been reset.</p>
            <Link to="/login" className="btn btn-primary btn-block" style={{ marginTop: 14 }}>
              Sign in
            </Link>
          </>
        ) : (
          <>
            <p className="sub">Pick something you haven&apos;t used before.</p>
            {!token && <p className="alert alert-err">This link is missing its reset token. Request a new one.</p>}
            <form onSubmit={submit}>
              <label className="field">
                <span>New password</span>
                <input
                  type="password"
                  required
                  minLength={MIN_PASSWORD_LENGTH}
                  autoComplete="new-password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
                {!password && <span className="help">Pick something only you would use.</span>}
                <PasswordRequirements password={password} />
              </label>
              {error && <p className="alert alert-err">{error}</p>}
              <button className="btn btn-primary btn-block btn-lg" disabled={!token || busy || !meetsPasswordPolicy(password)}>
                {busy ? 'Resetting…' : 'Reset password'}
              </button>
            </form>
          </>
        )}
      </div>

      <div className="auth-alt">
        <Link to="/forgot-password">Request a new reset link</Link>
      </div>
    </div>
  )
}
