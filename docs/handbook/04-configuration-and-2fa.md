[← Handbook index](README.md) · [Project README](../../README.md)

# 4. Configuration, secrets, email & 2FA

## The one rule

**No secret, token, key, connection string, or client id is ever committed.** `appsettings.json`
holds only **non-secret defaults and structure** (log levels, JWT issuer/audience/`kid`, token
lifetimes, demo seed *emails*). The only tracked config *file* with placeholder-ish values is
`.env.example` (allow-listed in `.gitleaks.toml`). `.gitignore` blocks `.env`, `secrets.json`,
keys/certs; gitleaks scans every push.

## Configuration precedence

ASP.NET Core layers configuration sources; **later wins**:

```
appsettings.json                         non-secret defaults & structure ONLY
   ↓ overridden by
.NET user-secrets (Development only)      LOCAL DEV — stored in your OS profile, never in the repo
   ↓ overridden by
environment variables                     the source of truth for real values, from:
                                            • GitHub Actions Variables / Secrets   (CI & build)
                                            • Azure App Service "Application settings"
                                              / Key Vault references                (production)
                                            • plain env vars, or a git-ignored .env  (Docker Compose)
```

So the intended flow for **anything sensitive or environment-specific** is: pull it from an
**environment source first** (GitHub, Azure, env vars); fall back to **user-secrets** for local
`dotnet run`; and put it in `appsettings.json` **only if it is a non-secret default**. Nothing
sensitive is hard-coded.

Keys map to env vars with the double-underscore convention that stands in for the `:` section
separator: `Jwt:SigningKey` → `Jwt__SigningKey`, `ConnectionStrings:WidgetWorks` →
`ConnectionStrings__WidgetWorks`, `Payments:Stripe:SecretKey` → `Payments__Stripe__SecretKey`.
(Azure App Service and GitHub both let you set these `__` names directly.)

The **web** app follows the same rule with Vite's `VITE_*` build-time variables: injected from
**GitHub Actions Variables** in CI or a git-ignored **`web/.env.local`** in dev — never committed.
The Google *client id* is public (it ships in the browser bundle) but is still kept out of source
by policy.

## What goes where — and why

| Setting (config key) | What it is | Where it lives | Why |
|---|---|---|---|
| `Jwt:SigningKey` | HMAC key for signing JWTs | env / GitHub Secret / Azure setting / Key Vault; user-secrets in dev | Secret; long-lived signing material. |
| `ConnectionStrings:WidgetWorks` or `Postgres:*` | DB connection / password | env / Azure setting / Key Vault; user-secrets or `.env` in dev | Secret; the DB password. |
| `Seed:DemoAdminPassword`, `Seed:DemoCustomerPassword` | Demo seed passwords | env / `.env` / user-secrets | Throwaway, documented — the sanctioned exception. |
| `Google:ClientId` | Google OAuth **client id** | env / GitHub Variable / Azure setting | **Public** (ships in the browser too); kept out of source by policy, not because it's secret. |
| `Payments:Provider` | `Mock` (default) or `Stripe` | env / appsettings | Not secret. |
| `Payments:Stripe:SecretKey` | Stripe secret key (`sk_test_`/`sk_live_`) | env / GitHub Secret / Azure setting / Key Vault | Secret; never committed. |
| `Payments:Stripe:WebhookSecret` | Stripe webhook signing secret (`whsec_`) | env / secret store | Secret. |
| `Email:Provider` | `Dev` (log) or `Smtp` | env / appsettings | Not secret. |
| `Email:Host/Port/Username/Password/...` | SMTP settings | env / secret store; user-secrets in dev | Password is secret. |
| `Cors:AllowedOrigins` | Browser origins allowed to call the API | env / appsettings | Not secret. |
| `App:BaseUrl` | Public SPA URL (used in email links) | env / appsettings | Not secret. |
| `VITE_API_BASE_URL`, `VITE_GOOGLE_CLIENT_ID` | Web build-time config | GitHub Actions **Variables** (CI) / `web/.env.local` (dev) | Public; injected at build time, never committed. |

**Why user-secrets vs `.env`:** user-secrets is read by the .NET app when you run it directly
(`dotnet run`, Development environment) — it lives in your OS profile, outside the repo. A
container can't read your host user-secrets, so the Docker path uses a git-ignored `.env` for
Compose variable substitution. In CI/prod, use **GitHub Actions Secrets** and **Azure App Service
settings / Key Vault**. See [`docs/local-development.md`](../local-development.md).

## Email setup

`IEmailSender` has two adapters, chosen by `Email:Provider`:

- **`Dev`** (default) — writes each message to **stdout** (the API container log). Great for
  local dev with no mail server; you'll see the password-reset link right in the logs.
- **`Smtp`** — real delivery via any SMTP host. Configure and it just works:

```
Email__Provider=Smtp
Email__Host=smtp.your-provider.com
Email__Port=587
Email__Username=apikey-or-user
Email__Password=<secret>          # env / secret store / user-secrets only
Email__UseStartTls=true
Email__FromAddress=no-reply@yourdomain.com
Email__FromName=WidgetWorks
```

Works with SendGrid, Mailgun, Postmark, Amazon SES, or a **local mail catcher**.

### Reading real mail locally (Mailpit)

`docker compose` ships a **Mailpit** service so you can exercise the true `Smtp` path — and
see the **HTML** bodies, which the `Dev` sender never shows — without an account or a
credential. Put this in `.env` and `docker compose up -d`:

```
Email__Provider=Smtp
Email__Host=mailpit
Email__Port=1025
Email__UseStartTls=false
```

`Email__UseStartTls=false` is the one that catches people: it defaults to **true**, but
Mailpit's 1025 is plain SMTP, so leaving it on makes every send fail. Read the captured mail
at **http://localhost:8025**.

Sending is **best-effort** at call sites: a failed email never rolls back a paid order or a
status change. Because callers swallow the error, `SmtpEmailSender` logs
`[email] FAILED …` before rethrowing — otherwise a misconfigured host is invisible. Production upgrade path (MailKit) is documented in
[ADR-023](../architecture/adr-023-transactional-email.md).

## Google sign-in setup

1. In Google Cloud Console, create an **OAuth 2.0 Client ID** (Web application) and add
   your web origin (e.g., `http://localhost:3000`) to authorized JavaScript origins.
2. Put the **client id** in two places (it's public):
   - API: `Google__ClientId=<id>.apps.googleusercontent.com`
   - Web: `VITE_GOOGLE_CLIENT_ID=<id>.apps.googleusercontent.com`
3. The browser uses Google Identity Services to get an **ID token** and POSTs it to
   `POST /auth/google`; the API validates it against Google's JWKS (issuer, audience =
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
