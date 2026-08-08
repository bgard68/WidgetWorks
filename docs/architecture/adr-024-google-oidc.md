# ADR-024 — Google OIDC sign-in

**Status:** Accepted
**Date:** August 8, 2026
**Context:** Users should be able to sign in with Google in addition to email/password.

## Decision

Use the **ID-token** flow suited to a SPA. The browser uses Google Identity Services to obtain a
Google **ID token**, then POSTs it to `POST /auth/google`. The backend:

1. **Validates the ID token** against Google's published JWKS via `IGoogleTokenValidator` — checking
   signature, issuer (`accounts.google.com`), audience (our configured client id), and lifetime. This
   reuses the already-referenced `Microsoft.IdentityModel` libraries, so **no new dependency** and no
   client secret is needed for verification. Signing keys are cached ~1 hour.
2. **Resolves the user** — by `google_sub`; else links Google to an existing account matched by email;
   else provisions a new `Customer` (no password) and sends a welcome email.
3. **Issues our own tokens** — the same short-lived access token + rotating refresh token as password
   login, so the rest of the system (security stamp, `kid` rotation, refresh reuse detection) applies
   uniformly. Unverified Google emails are refused.

## Secrets & config

The Google **client id** is public and lives in `Google:ClientId` config; it is not a secret but is
kept out of source per the project directive. No client secret is stored — the ID-token flow doesn't
need one server-side. If a future server-side authorization-code flow is added, its client secret goes
in user-secrets / deploy config only, never the repo (already enforced by `.gitleaks.toml`).

## Notes

Google accounts carry their own MFA, so Google sign-in issues tokens directly rather than running our
TOTP challenge. A user can have both a password (with local 2FA) and a linked Google identity.
