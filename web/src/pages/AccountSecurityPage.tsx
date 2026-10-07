import { useEffect, useState } from 'react'
import { AccountNav } from '../components/AccountNav'
import { PasswordField } from '../components/PasswordField'
import { PasswordRequirements } from '../components/PasswordRequirements'
import { meetsPasswordPolicy } from '../lib/passwordPolicy'
import { useProfile } from '../account/useProfile'
import { api, setSession } from '../api/client'
import type { AuthResponse, ProfileView } from '../api/types'

/**
 * Login and security.
 *
 * Named after Company A's tile, which describes itself as "Edit login, name, and mobile number" — the
 * display name lives here rather than on a profile page of its own. That is the one thing Company A and
 * Company B agree on: neither has a standalone Profile destination. Company A folds the name into
 * security; Company B folds it into Account Settings alongside addresses. A page holding a single text
 * box would have needed justifying.
 *
 * Email is shown but not editable. Changing it safely means confirming to the *new* address before
 * switching, which is a token flow of its own — doing it naively turns a profile edit into an
 * account-takeover step.
 */
export function AccountSecurityPage() {
  const { profile, error: loadError, setProfile } = useProfile()

  return (
    <div className="acct">
      <AccountNav profile={profile} current="security" />

      <div className="acct-main">
        <h1>Login and security</h1>
        {loadError && <p className="alert alert-err">{loadError}</p>}
        {profile && (
          <>
            <NameSection profile={profile} onSaved={setProfile} />
            <EmailSection profile={profile} />
            {profile.hasPassword && <PasswordSection />}
            <TwoFactorSection profile={profile} />
          </>
        )}
      </div>
    </div>
  )
}

function NameSection({ profile, onSaved }: { profile: ProfileView; onSaved: (p: ProfileView) => void }) {
  const [name, setName] = useState(profile.displayName ?? '')
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => { setName(profile.displayName ?? '') }, [profile.displayName])

  async function save(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setSaved(false)
    setBusy(true)
    try {
      const updated = await api<ProfileView>('/account/profile', { method: 'PUT', body: { displayName: name } })
      onSaved(updated)
      setSaved(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save your name.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel acct-section" onSubmit={save}>
      <div className="panel-body stack">
        <h2>Your name</h2>
        <p className="help">Used to greet you and on your orders. Leave it blank if you would rather not say.</p>
        <label className="field">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={60} placeholder="Jane Doe" />
        </label>
        {error && <p className="alert alert-err">{error}</p>}
        {saved && <p className="alert alert-ok">Name saved.</p>}
        <div className="row">
          <button className="btn btn-primary btn-sm" disabled={busy}>{busy ? 'Saving…' : 'Save name'}</button>
        </div>
      </div>
    </form>
  )
}

function EmailSection({ profile }: { profile: ProfileView }) {
  return (
    <div className="panel acct-section">
      <div className="panel-body stack">
        <h2>Email</h2>
        <div className="sumrow"><span>Sign in with</span><span>{profile.email}</span></div>
        {/* Said plainly rather than shown as a disabled button with no explanation. */}
        <p className="help">
          Changing the address you sign in with is not supported yet — it needs a confirmation sent to
          the new address before the switch, so that it cannot be used to take an account over.
        </p>
      </div>
    </div>
  )
}

function PasswordSection() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const ready = current.length > 0 && meetsPasswordPolicy(next)

  async function save(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setDone(false)
    setBusy(true)
    try {
      // The change signs every session out, this one included — so the server hands back a fresh
      // pair and we swap them in. Without this the page would appear to log you out on success.
      const tokens = await api<AuthResponse>('/account/password', {
        method: 'POST',
        body: { currentPassword: current, newPassword: next },
      })
      setSession({ accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, role: tokens.role })
      setCurrent('')
      setNext('')
      setDone(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not change your password.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel acct-section" onSubmit={save}>
      <div className="panel-body stack">
        <h2>Password</h2>
        <p className="help">Changing it signs you out everywhere else.</p>

        <PasswordField label="Current password" value={current} onChange={setCurrent} autoComplete="current-password" />
        <PasswordField label="New password" value={next} onChange={setNext} autoComplete="new-password">
          <PasswordRequirements password={next} />
        </PasswordField>

        {error && <p className="alert alert-err">{error}</p>}
        {done && <p className="alert alert-ok">Password changed. Other devices have been signed out.</p>}

        <div className="row">
          <button className="btn btn-primary btn-sm" disabled={busy || !ready}>
            {busy ? 'Changing…' : 'Change password'}
          </button>
        </div>
      </div>
    </form>
  )
}

function TwoFactorSection({ profile }: { profile: ProfileView }) {
  const [signedOut, setSignedOut] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function signOutEverywhere() {
    setError(null)
    setBusy(true)
    try {
      await api('/auth/secure-account', { method: 'POST' })
      setSignedOut(true)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not sign out your other sessions.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel acct-section">
      <div className="panel-body stack">
        <h2>Two-step verification</h2>
        <div className="sumrow">
          <span>Status</span>
          <span>{profile.twoFactorEnabled ? 'On — authenticator app' : 'Off'}</span>
        </div>
        <p className="help">
          {profile.twoFactorEnabled
            ? 'Keep your recovery codes somewhere safe. Losing both your authenticator and your codes means staff have to reset it for you.'
            : 'Adds a code from an authenticator app on top of your password.'}
        </p>

        <h2 style={{ marginTop: 10 }}>Sign out everywhere</h2>
        <p className="help">Ends every session on every device, including this one.</p>
        {error && <p className="alert alert-err">{error}</p>}
        {signedOut && <p className="alert alert-ok">Every other session has been ended.</p>}
        <div className="row">
          <button type="button" className="btn btn-danger btn-sm" disabled={busy} onClick={signOutEverywhere}>
            {busy ? 'Signing out…' : 'Sign out all devices'}
          </button>
        </div>
      </div>
    </div>
  )
}
