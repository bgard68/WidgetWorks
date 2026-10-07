/**
 * What makes a password acceptable, mirrored from the server.
 *
 * The authority is `PasswordPolicy` in `src/WidgetWorks.Domain/Users/PasswordPolicy.cs`, and every
 * rule here is enforced there — this copy exists so the checklist can tick off as someone types,
 * which a round trip per keystroke could not do. If you change one side, change the other: the
 * server will still refuse a bad password either way, but a checklist that disagrees with it is
 * worse than no checklist, because it tells people they are done when they are not.
 */
export const MIN_PASSWORD_LENGTH = 10

export interface PasswordRule {
  /** Stable identifier, matching the server's rule code. */
  code: string
  /** The rule as a person reads it. */
  requirement: string
  isMet: (password: string) => boolean
}

export const PASSWORD_RULES: PasswordRule[] = [
  { code: 'length', requirement: `At least ${MIN_PASSWORD_LENGTH} characters`, isMet: (p) => p.length >= MIN_PASSWORD_LENGTH },
  { code: 'lowercase', requirement: 'A lowercase letter', isMet: (p) => /[a-z]/.test(p) },
  { code: 'uppercase', requirement: 'An uppercase letter', isMet: (p) => /[A-Z]/.test(p) },
  { code: 'digit', requirement: 'A number', isMet: (p) => /[0-9]/.test(p) },

  // Anything that is not a letter, a digit or whitespace — defined by exclusion, like the server, so
  // an unusual but perfectly good passphrase character is not rejected for not being on a list.
  { code: 'symbol', requirement: 'A symbol, such as ! ? # or @', isMet: (p) => /[^A-Za-z0-9\s]/.test(p) },
]

/** True once every rule is satisfied. */
export function meetsPasswordPolicy(password: string): boolean {
  return PASSWORD_RULES.every((rule) => rule.isMet(password))
}
