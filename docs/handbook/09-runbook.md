[← Handbook index](README.md) · [Project README](../../README.md)

# 9. Runbook — testing & going live (email, payments, Google)

Concrete steps to exercise each integration locally with **no external accounts**, and
exactly what changes to run it **for real**. All values follow the
[configuration & secrets rules](04-configuration-and-2fa.md): in dev set them with
`dotnet user-secrets` (or environment variables); in production use a secret store — never
`appsettings.json`.

**API base URL used below:** `http://localhost:8080` for the Docker stack, or
`http://localhost:5080` for [hybrid dev](03-setup-and-run.md). Adjust to your setup.
Setting a dev value looks like either of:

```bash
# from src/WidgetWorks.WebApi (Development environment) — persists across runs
dotnet user-secrets set "Payments:Provider" "Stripe"

# or an environment variable (loads in any environment) — the __ replaces the : separator
#   PowerShell:  $env:Payments__Provider = "Stripe"
#   bash:        export Payments__Provider=Stripe
```

Restart the API after changing config.

---

## Health probes — which one to point at what

Two endpoints answering two different questions. Wiring the wrong one is how a monitoring signal
ends up unable to report bad news.

| Endpoint | Answers | Touches the database | Point this at |
|---|---|---|---|
| `GET /health` | Did this process start correctly? | **No** | Keep-warm pings, first-boot provisioning checks |
| `GET /health/ready` | Can this instance serve a request right now? | **Yes** — `select 1` | Platform health probe, alerting, load-balancer rotation |

```bash
curl -i http://localhost:8080/health          # {"status":"ok",...}
curl -i http://localhost:8080/health/ready    # {"status":"ready","database":"ok",...}
```

**Why they are separate, and why it matters for the bill.** `/health` is pinged every few minutes to
hold a free-tier App Service instance loaded. If that ping woke the database each time it would hold
a metered resource awake around the clock — roughly 180 CU-hrs against Neon's 100 CU-hr monthly free
allowance. Warming the app while letting the database sleep is deliberate.

So: **never point a scheduled warm-up at `/health/ready`**, and never make `/health` query the
database. They look interchangeable and are not.

`/health/ready` returns `503` with the failing exception *type* when the database does not answer —
never the message, because a connection error can carry a host name or a user and the endpoint is
anonymous.

## Tracing a failure a customer reports

Every response carries an `X-Correlation-Id` header, and a `500` repeats it in the body:

```json
{ "error": "Something went wrong on our side. Quote the reference below if you contact us.",
  "correlationId": "0HN7…" }
```

The same id is on the log line for that request, so a customer report becomes a lookup rather than a
search through everything that happened at that minute:

```bash
# Azure App Service log stream, or wherever logs land
az webapp log tail --name <app> --resource-group <rg> | grep 0HN7
```

If the caller supplied `X-Correlation-Id`, that value is kept so a trace spans several services —
sanitised first, because it reaches log messages and text carrying newlines could otherwise forge
whole entries.

## Email

### Test locally — Dev sender (default, zero setup)

`Email:Provider` defaults to **`Dev`**, which writes every message to the **API log
(stdout)** instead of sending it.

1. In the store, use **Sign in → Forgot password** (or just place an order for a receipt).
2. Read the full email — including the password-reset link — in the API terminal.

So the reset link points at your SPA, set the public base URL (Docker SPA is `:3000`,
hybrid-dev Vite is `:5173`):

```bash
dotnet user-secrets set "App:BaseUrl" "http://localhost:5173"
```

### Test with a real inbox UI — Mailpit

A local SMTP catcher gives you a browser inbox — with the real HTML — without sending
anything externally. **`docker compose` already runs one**, so under Docker you only need to
point the app at it. In `.env`:

```
Email__Provider=Smtp
Email__Host=mailpit
Email__Port=1025
Email__UseStartTls=false
```

Running the API on the host instead (hybrid dev), the same settings go to user-secrets and
the host is `localhost`:

```bash
dotnet user-secrets set "Email:Provider" "Smtp"
dotnet user-secrets set "Email:Host" "localhost"
dotnet user-secrets set "Email:Port" "1025"
dotnet user-secrets set "Email:UseStartTls" "false"
```

**`UseStartTls` must be `false`** — port 1025 is plain SMTP, and leaving STARTTLS on makes
every send fail. Trigger an email and read it at **http://localhost:8025**.

### Go live — real SMTP

Point at any provider (SendGrid, Mailgun, SES, Postmark) via the **secret store**:

```
Email__Provider=Smtp
Email__Host=smtp.sendgrid.net
Email__Port=587
Email__Username=apikey
Email__Password=<real key>            # secret
Email__UseStartTls=true
Email__FromAddress=no-reply@yourdomain.com
Email__FromName=WidgetWorks
```

Provider-side, verify your sending domain (SPF/DKIM) or mail will land in spam. Sending is
best-effort — a failed email never rolls back a paid order or a status change.

---

## Payments

### Test locally — Mock gateway (default, no card, no charge)

`Payments:Provider` defaults to **`Mock`**. In the checkout UI:

- **Card** or **Google Pay** → order settles immediately to **Paid**.
- **Klarna — Pay later** → order becomes **AwaitingPayment**; on the confirmation page click
  **Approve payment** (or **Simulate decline**) to fire the webhook and settle it.
- **Test: declined card** → the decline path.

Or settle an async order by hand:

```bash
curl -X POST http://localhost:8080/webhooks/payments/mock \
  -H 'Content-Type: application/json' \
  -d '{"reference":"<paymentReference from checkout>","outcome":"succeeded"}'
```

### Test with Stripe test mode (real integration, still no money)

```bash
dotnet user-secrets set "Payments:Provider" "Stripe"
dotnet user-secrets set "Payments:Stripe:SecretKey" "sk_test_...."
```

> **Note:** the SPA's buttons send *demo* tokens that only the Mock gateway understands, so
> they won't work against Stripe (real card entry needs Stripe's Payment Element — the
> documented drop-in not yet wired into the SPA). To test **Stripe**, drive the API directly
> from the **Scalar UI** (`/scalar/v1`) or curl, sending a Stripe **test PaymentMethod** as
> the `paymentToken`:
>
> - `pm_card_visa` → succeeds
> - `pm_card_chargeDeclined` → declined

For the async/redirect webhook in test mode, forward events with the Stripe CLI (it prints a
signing secret):

```bash
stripe listen --forward-to localhost:8080/webhooks/payments/stripe
dotnet user-secrets set "Payments:Stripe:WebhookSecret" "whsec_...."
```

### Go live — real payments

Same integration, your **live** keys via the **secret store** only:

```
Payments__Provider=Stripe
Payments__Stripe__SecretKey=sk_live_....       # secret
Payments__Stripe__WebhookSecret=whsec_....     # secret
```

1. In the Stripe dashboard, register your production webhook endpoint
   `https://yourdomain/webhooks/payments/stripe` and use the signing secret it gives you.
2. Wire Stripe's **Payment Element** into the SPA checkout so real cards can be entered
   (the drop-in point; the current demo tokens are Mock-only).
3. `.gitleaks.toml` blocks committing `sk_live_*` — keys must come from secrets.

See [Payments](05-payments.md) for the full model (async settlement, webhooks, test cards).

---

## Google sign-in (OAuth)

The sign-in button needs a real **OAuth 2.0 Client ID** — there's no offline stand-in.

### Create the client + test locally

1. Google Cloud Console → **APIs & Services → Credentials → Create OAuth client ID →
   Web application**.
2. Under **Authorized JavaScript origins**, add your SPA origin
   (`http://localhost:5173` for hybrid dev, or `http://localhost:3000` for Docker).
3. Set the client id (public, `…apps.googleusercontent.com`) in **both** the API and the web app:

```bash
# API (Development):
dotnet user-secrets set "Google:ClientId" "<id>.apps.googleusercontent.com"
```

```
# web/.env.local (git-ignored), then restart `npm run dev`:
VITE_API_BASE_URL=http://localhost:5080
VITE_GOOGLE_CLIENT_ID=<id>.apps.googleusercontent.com
```

Reload the store and use **Continue with Google**. The browser gets an ID token and posts it
to `POST /auth/google`; the API validates it against Google's JWKS (issuer, audience = your
client id, signature, expiry) — **no client secret** is used in this flow.

### Go live

- Add your production origin (`https://yourdomain`) to Authorized JavaScript origins, and
  **publish** the OAuth consent screen (Google verifies it for external users).
- Supply the client id as `Google__ClientId` on the API (a GitHub Actions **Variable** /
  Azure App Service setting) and as `VITE_GOOGLE_CLIENT_ID` for the web build.

See [Configuration → Google sign-in setup](04-configuration-and-2fa.md#google-sign-in-setup).

---

## Where values go — dev vs. live (summary)

| | Local dev | CI / build | Production |
|---|---|---|---|
| Non-secrets (provider flags, client id, `VITE_*`) | user-secrets / `.env.local` | GitHub Actions **Variables** | Azure App Service **Application settings** |
| Secrets (`sk_live_`, SMTP password, `whsec_`, DB password, JWT key) | user-secrets / git-ignored `.env` | GitHub Actions **Secrets** | Azure **Application settings** / **Key Vault** |

Never `appsettings.json`. Full precedence and the complete key table:
[Configuration, secrets, email & 2FA](04-configuration-and-2fa.md).
