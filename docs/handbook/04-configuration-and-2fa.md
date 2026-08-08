# 4. Configuration, secrets, email & 2FA

## The one rule

**No secret is ever committed.** The only tracked config file is `.env.example` (placeholders,
allow-listed in `.gitleaks.toml`). `.gitignore` blocks `.env`, `secrets.json`, keys/certs;
gitleaks scans every push.

## Configuration precedence

ASP.NET Core layers sources; later wins:

```
appsettings.json  <  user-secrets (Development only)  <  environment variables
```

Keys use the double-underscore convention that maps to config sections:
`Jwt__SigningKey` → `Jwt:SigningKey`.

## What goes where — and why

| Setting (config key) | What it is | Where it lives | Why |
|---|---|---|---|
| `Jwt:SigningKey` | HMAC key for signing JWTs | user-secrets (dev) / env / Key Vault (prod) | Secret; long-lived signing material. |
| `ConnectionStrings:WidgetWorks` or `Postgres:*` | DB connection / password | user-secrets or `.env` | Secret; the DB password. |
| `Seed:DemoAdminPassword`, `Seed:DemoCustomerPassword` | Demo seed passwords | `.env` / user-secrets | Throwaway, documented — the sanctioned exception. |
| `Google:ClientId` | Google OAuth **client id** | `.env` / env | **Public** (ships in the browser too); kept out of source by policy, not because it’s secret. |
| `Payments:Provider` | `Mock` (default) or `Stripe` | `.env` / appsettings | Not secret. |
| `Payments:Stripe:SecretKey` | Stripe **test** secret key | user-secrets / env | Secret; test keys only, never live. |
| `Email:Provider` | `Dev` (log) or `Smtp` | `.env` / appsettings | Not secret. |
| `Email:Host/Port/Username/Password` | SMTP settings | user-secrets / env | Password is secret. |
| `Cors:AllowedOrigins` | Browser origins allowed to call the API | `.env` / appsettings | Not secret. |
| `App:BaseUrl` | Public SPA URL (used in email links) | `.env` / appsettings | Not secret. |
| `VITE_API_BASE_URL`, `VITE_GOOGLE_CLIENT_ID` | Web build-time config | GitHub Actions **Variables** (CI) / `web/.env.local` (dev) | Public; injected at build time, never committed. |

**Why user-secrets vs `.env`:** user-secrets is read by the .NET app when you run it
directly (`dotnet run`) — it lives in your OS profile, outside the repo, and is dev-only.
A container can’t read your host user-secrets, so the Docker path uses a git-ignored
`.env` for Compose variable substitution. In CI/prod, use Actions Secrets / Key Vault.
See [`docs/local-development.md`](../local-development.md).

## Email setup

`IEmailSender` has two adapters, chosen by `Email:Provider`:

- **`Dev`** (default) — writes each message to **stdout** (the API container log). Great for
  local dev with no mail server; you’ll see the password-reset link right in the logs.
- **`Smtp`** — real delivery via any SMTP host. Configure and it just works:

```
Email__Provider=Smtp
Email__Host=smtp.your-provider.com
Email__Port=587
Email__Username=apikey-or-user
Email__Password=<secret>          # user-secrets / env only
Email__UseStartTls=true
Email__FromAddress=no-reply@yourdomain.com
Email__FromName=WidgetWorks
```

Works with SendGrid, Mailgun, Postmark, Amazon SES, or a **local mail catcher**
(Mailpit/MailHog) for offline testing — point `Email__Host` at it and read the captured
mail in its UI. Sending is **best-effort** at call sites: a failed email never rolls back
a paid order or a status change. Production upgrade path (MailKit) is documented in
[ADR-023](../architecture/adr-023-transactional-email.md).

## Google sign-in setup

1. In Google Cloud Console, create an **OAuth 2.0 Client ID** (Web application) and add
   your web origin (e.g., `http://localhost:3000`) to authorized JavaScript origins.
2. Put the **client id** in two places (it’s public):
   - API: `Google__ClientId=<id>.apps.googleusercontent.com`
   - Web: `VITE_GOOGLE_CLIENT_ID=<id>.apps.googleusercontent.com`
3. The browser uses Google Identity Services to get an **ID token** and POSTs it to
   `POST /auth/google`; the API validates it against Google’s JWKS (issuer, audience =
   your client id, signature, expiry), then finds/links/creates the user and issues our
   own tokens. **No client secret** is used in this flow. See
   [ADR-024](../architecture/adr-024-google-oidc.md).

## How to set up 2FA (TOTP)

2FA is real TOTP (authenticator apps like Google Authenticator, Authy, 1Password). A
signed-in user enables it like this:

1. **Start enrollment** — `POST /2fa/enroll` (authenticated). Returns:
   - `secretBase32` — the shared secret, and
   - `otpAuthUri` — an `otpauth://totp/…` URI you can render as a **QR code** or paste into
     the authenticator app.
2. **Add it to your authenticator** — scan the QR / enter the secret. The app now shows a
   rotating 6-digit code (SHA-1, 30-second period).
3. **Confirm** — `POST /2fa/enroll/confirm` with `{ "code": "<current 6 digits>" }`. On
   success it returns **10 single-use recovery codes** (store them safely) and enables 2FA.
   Enabling 2FA rotates your security stamp, so existing sessions are signed out.
4. **From now on, login is two-step** — `POST /auth/login` returns
   `{ twoFactorRequired: true, challengeToken }`; submit the current code to
   `POST /auth/2fa` with `{ challengeToken, code }` to get tokens. Lost your device?
   Use a recovery code at `POST /auth/2fa/recovery`.
5. **Disable** — `POST /2fa/disable` (authenticated).

The end-to-end smoke test (`scripts/smoke-test.ps1`) performs this whole flow
programmatically — it computes real TOTP codes, enrolls, confirms, and completes a
challenge login — so you can see it working. Underlying design:
[ADR / arch §6.4](../architecture/02-Architecture-and-Technical-Design.md).
