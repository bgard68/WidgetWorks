import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { PasswordRequirements } from '../components/PasswordRequirements'
import { MIN_PASSWORD_LENGTH, meetsPasswordPolicy } from '../lib/passwordPolicy'
import { PasswordField } from '../components/PasswordField'

export function RegisterPage() {
  const { register, login } = useAuth()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // The server refuses a weak password either way; this only decides whether the button is worth
  // pressing yet, so the shopper is not told no after submitting something they could see was short.
  const passwordOk = meetsPasswordPolicy(password)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      await register(email, password)
      await login(email, password)
      navigate('/store')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Registration failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="authpage">
      <div className="authcard">
        <h1>Create your account</h1>
        <p className="sub">One account for orders, tracking and faster checkout.</p>
        <form onSubmit={submit}>
          <label className="field">
            <span>Email address</span>
            <input type="email" required autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
          </label>
          <PasswordField
            label="Password"
            value={password}
            onChange={setPassword}
            autoComplete="new-password"
            minLength={MIN_PASSWORD_LENGTH}
          >
            {!password && <span className="help">Pick something only you would use.</span>}
            <PasswordRequirements password={password} />
          </PasswordField>
          {error && <p className="alert alert-err">{error}</p>}
          <button className="btn btn-primary btn-block btn-lg" disabled={busy || !passwordOk}>
            {busy ? 'Creating account…' : 'Create account'}
          </button>
        </form>
      </div>

      <div className="auth-alt">
        Already have an account? <Link to="/login">Sign in</Link>
      </div>
    </div>
  )
}
