import { PASSWORD_RULES } from '../lib/passwordPolicy'

/**
 * The password rules, ticked off as they are satisfied.
 *
 * Every rule is listed from the start rather than appearing as it fails, because the point is to tell
 * someone what to type before they get it wrong — a message that shows up only after a rejected
 * submit is the thing this replaces.
 *
 * Accessibility is most of the work here, and it is why the state is not conveyed by colour alone:
 *
 * - Each row carries a ✓ or ✗ glyph as well as a colour, so it reads correctly to anyone who cannot
 *   distinguish red from green — about one man in twelve.
 * - The glyph is `aria-hidden` and each row has its own "Met"/"Not met" text for screen readers,
 *   since "✓" announces as "check mark" or nothing at all depending on the reader.
 * - The list is `aria-live="polite"`, so a reader announces rows as they change instead of leaving a
 *   non-sighted user to submit and hope. Polite rather than assertive: this should not interrupt
 *   someone mid-word while typing.
 *
 * Nothing is shown until there is something to say. An untouched field with five red crosses against
 * it reads as a telling-off before the shopper has done anything wrong.
 */
export function PasswordRequirements({ password }: { password: string }) {
  if (!password) {
    return null
  }

  return (
    <ul className="pwreqs" aria-live="polite" aria-label="Password requirements">
      {PASSWORD_RULES.map((rule) => {
        const met = rule.isMet(password)
        return (
          <li key={rule.code} className={met ? 'pwreq met' : 'pwreq unmet'}>
            <span className="pwreq-ico" aria-hidden="true">{met ? '✓' : '✗'}</span>
            <span>{rule.requirement}</span>
            <span className="sr-only">{met ? ' — met' : ' — not met'}</span>
          </li>
        )
      })}
    </ul>
  )
}
