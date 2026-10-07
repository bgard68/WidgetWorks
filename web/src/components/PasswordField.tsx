import { useId, useState } from 'react'

interface PasswordFieldProps {
  label: string
  value: string
  onChange: (value: string) => void
  /** `current-password` when signing in, `new-password` when setting one. */
  autoComplete: 'current-password' | 'new-password'
  minLength?: number
  /** Rendered under the input — the requirements checklist, or a hint. */
  children?: React.ReactNode
}

/**
 * A password box with a show/hide toggle inside it.
 *
 * One component rather than the same markup in three pages, because the fiddly parts are easy to get
 * subtly wrong and a toggle that behaves differently on the login page than the register page is its
 * own small bug.
 *
 * The details that matter:
 *
 * - **`type="button"`.** Without it the toggle is a submit button, and clicking the eye would try to
 *   sign you in with a half-typed password. This is the classic version of this bug.
 * - **An explicit `<label htmlFor>` rather than a wrapping one.** The toggle has to sit inside the
 *   box, and a `<button>` inside a `<label>` is interactive content where the HTML spec does not
 *   allow it — browsers cope, assistive technology sometimes does not.
 * - **The label changes with the state, not just the icon.** It reads "Show password" while hidden
 *   and "Hide password" while shown, so a screen reader announces the action rather than "button".
 *   `aria-pressed` carries the state as well, which is what a toggle is.
 * - **It starts hidden, every time.** The state is local, so navigating away and back re-hides it
 *   rather than quietly leaving a password on screen.
 * - **`tabIndex={-1}`.** Deliberately *not* set — someone navigating by keyboard should be able to
 *   reach it, which is exactly who benefits from checking what they typed.
 */
export function PasswordField({ label, value, onChange, autoComplete, minLength, children }: PasswordFieldProps) {
  const id = useId()
  const [shown, setShown] = useState(false)

  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>{label}</label>
      <div className="pwbox">
        <input
          id={id}
          type={shown ? 'text' : 'password'}
          required
          minLength={minLength}
          autoComplete={autoComplete}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
        <button
          type="button"
          className="pwbox-toggle"
          onClick={() => setShown(!shown)}
          aria-label={shown ? 'Hide password' : 'Show password'}
          aria-pressed={shown}
          title={shown ? 'Hide password' : 'Show password'}
        >
          {shown ? <EyeClosedIcon /> : <EyeIcon />}
        </button>
      </div>
      {children}
    </div>
  )
}

/**
 * Inline SVG rather than an icon font or an emoji: it inherits `currentColor` so it follows the
 * theme, and 👁 renders as wildly different things across platforms — on some it is a full-colour
 * eyeball.
 */
function EyeIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
      <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  )
}

/** The same eye, struck through — the convention everywhere, so it needs no explaining. */
function EyeClosedIcon() {
  return (
    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
      <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="12" cy="12" r="3" />
      <path d="M3 21 21 3" strokeLinecap="round" />
    </svg>
  )
}
