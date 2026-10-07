import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { PasswordField } from './PasswordField'
import { renderWithProviders } from '../test/render'

/**
 * The show/hide toggle.
 *
 * Small, and worth pinning down anyway: the failure modes here are the kind that get shipped because
 * the happy path looks fine. A toggle that submits the form, one that announces nothing to a screen
 * reader, or one that leaves a password on screen after you navigate away all look identical to a
 * working one if you only click it once and watch the dots turn into letters.
 */
describe('PasswordField', () => {
  function render(label = 'Password') {
    return renderWithProviders(
      <form onSubmit={(e) => { e.preventDefault(); submitted.count += 1 }}>
        <PasswordField label={label} value="hunter2" onChange={() => {}} autoComplete="current-password" />
        <button type="submit">Sign in</button>
      </form>,
      { at: '/login' },
    )
  }

  const submitted = { count: 0 }

  it('starts hidden and reveals the password when the eye is clicked', async () => {
    submitted.count = 0
    const user = userEvent.setup()
    render()

    const input = screen.getByLabelText('Password')
    expect(input).toHaveAttribute('type', 'password')

    await user.click(screen.getByRole('button', { name: 'Show password' }))
    expect(input).toHaveAttribute('type', 'text')

    // And back again — the same control, relabelled to say what it will do next.
    await user.click(screen.getByRole('button', { name: 'Hide password' }))
    expect(input).toHaveAttribute('type', 'password')
  })

  it('does not submit the form it sits in', async () => {
    submitted.count = 0
    const user = userEvent.setup()
    render()

    await user.click(screen.getByRole('button', { name: 'Show password' }))

    // The classic version of this bug: a button inside a form defaults to type="submit", so clicking
    // the eye would try to sign in with a half-typed password.
    expect(submitted.count).toBe(0)
  })

  it('tells assistive technology what it does and what state it is in', async () => {
    submitted.count = 0
    const user = userEvent.setup()
    render()

    const toggle = screen.getByRole('button', { name: 'Show password' })

    // aria-pressed is what makes this a toggle rather than an unlabelled button that does something.
    expect(toggle).toHaveAttribute('aria-pressed', 'false')

    await user.click(toggle)
    expect(screen.getByRole('button', { name: 'Hide password' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('labels the input properly even though the toggle sits inside the box', () => {
    submitted.count = 0
    render('New password')

    // An explicit label/htmlFor pairing rather than a wrapping <label>, because a button inside a
    // label is interactive content where the spec does not allow it. The label must still work.
    const input = screen.getByLabelText('New password')
    expect(input).toHaveAttribute('autocomplete', 'current-password')
    expect(screen.getByText('New password').tagName).toBe('LABEL')
  })

  it('re-hides the password when the field is mounted again', async () => {
    submitted.count = 0
    const user = userEvent.setup()
    const { unmount } = render()

    await user.click(screen.getByRole('button', { name: 'Show password' }))
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'text')

    unmount()
    render()

    // State is local on purpose: navigating away and back must not leave a password on screen.
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password')
  })
})
