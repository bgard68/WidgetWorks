# WidgetWorks

An end-to-end online **widget store** — a portfolio showcase built to a production security
posture. It demonstrates the hard parts most demos skip: real auth (JWT + rotating refresh
+ per-user security stamp), **TOTP 2FA** and Google sign-in, catalog/inventory with atomic
stock reservation, server-side re-priced checkout with **pluggable payments** (mock +
Stripe, sync and async/webhook), transactional email, and a full order lifecycle — on
clean, testable, time-abstracted code.

**Stack:** .NET 10 (ASP.NET Core Minimal API) · Dapper + PostgreSQL 16 · React + TypeScript
(Vite) · Onion/Clean architecture · Docker Compose.

---

## Quick start (Docker — one command)

You don't need .NET or Node installed; Docker builds both.

```bash
git clone https://github.com/bgard68/WidgetWorks.git
cd WidgetWorks
cp .env.example .env      # placeholder values are valid for a local run
docker compose up --build
```

| What | URL |
|---|---|
| Store (SPA) | http://localhost:3000 |
| API + Scalar (interactive API UI) | http://localhost:8080/scalar/v1 |
| Health | http://localhost:8080/health |

Migrations and demo seed run automatically on API start. For running the API on the host
with fast iteration (and the exact port/user-secrets details), see
**[Setup & run](docs/handbook/03-setup-and-run.md)**.

### Demo accounts (local demo only)

| Role | Email | Password |
|------|-------|----------|
| Administrator (immutable) | `admin@widgetworks.demo` | `DemoAdmin!Change01` |
| Customer | `demo@widgetworks.demo` | `DemoUser!Change01` |

Passwords are set from `.env` / user-secrets at seed time — the one sanctioned, documented
"credential" in the repo. The admin has no 2FA by default, so it logs straight in.

---

## Documentation

The full engineering handbook lives in **[`docs/handbook/`](docs/handbook/README.md)**.
Start at the index, or jump straight to a chapter:

| # | Doc | Covers |
|---|-----|--------|
| — | **[Handbook index](docs/handbook/README.md)** | Table of contents for everything below |
| 1 | [Overview](docs/handbook/01-overview.md) | What it is, features, tech stack, repo layout |
| 2 | [Architecture](docs/handbook/02-architecture.md) | Onion/clean layering, request flow, security model, seams |
| 3 | [Setup & run](docs/handbook/03-setup-and-run.md) | Docker + hybrid dev, ports, demo accounts, troubleshooting |
| 4 | [Configuration, secrets, email & 2FA](docs/handbook/04-configuration-and-2fa.md) | What keys go where/how/why; **email setup**; Google setup; how to set up 2FA |
| 5 | [Payments & testing cards](docs/handbook/05-payments.md) | Mock + Stripe, async/webhooks, **testing without charging a card**, sales tax, going live |
| 6 | [Database & schema](docs/handbook/06-database.md) | Why Postgres, migrations, tables & relationships |
| 7 | [Testing & smoke test](docs/handbook/07-testing.md) | Unit tests, CI gates, the end-to-end smoke test |
| 8 | [Bugs & lessons learned](docs/handbook/08-bugs-and-lessons.md) | Real bugs: how found, fixed, prevented |
| 9 | [Runbook — testing & going live](docs/handbook/09-runbook.md) | **Step-by-step to test email, payments & Google locally, and how to configure each for real** |
| 10 | [Deploying to Azure on free tiers](docs/handbook/10-deploy-azure-free.md) | Running the whole stack for $0 — F1 App Service, Static Web Apps, Key Vault + managed identity, Postgres on Neon |

Other docs: **[Security policy](SECURITY.md)** · **[Local development notes](docs/local-development.md)** · **[Web app README](web/README.md)** · **[Architecture ADRs](docs/architecture/)**.

---

## Configuration & secrets — the policy

**No secret, token, key, connection string, or client id is ever committed.** `appsettings.json`
holds only **non-secret defaults and structure** (log levels, token lifetimes, the demo
seed *emails*, the JWT issuer/audience/`kid`). Everything sensitive — or deployment-specific
— is read from an **environment source**, in this precedence (later wins):

```
appsettings.json (non-secret defaults)
   ↓  overridden by
.NET user-secrets            (LOCAL DEV ONLY — outside the repo, in your OS profile)
   ↓  overridden by
environment variables        ← the source for real deployments:
                               • GitHub Actions Variables / Secrets  (CI/build)
                               • Azure App Service "Application settings" / Key Vault references (prod)
                               • plain env vars / a git-ignored .env for Docker Compose
```

Config keys map to env vars with the double-underscore convention:
`Jwt:SigningKey` → `Jwt__SigningKey`, `ConnectionStrings:WidgetWorks` →
`ConnectionStrings__WidgetWorks`, etc. The **web** app follows the same rule: `VITE_*`
values are injected at **build time** from GitHub Actions Variables (CI) or a git-ignored
`web/.env.local` (dev) — never committed (the Google *client id* is public, but still kept
out of source by policy).

Full table of every setting, where it belongs, and why: **[Configuration & secrets](docs/handbook/04-configuration-and-2fa.md)**.
See also **[SECURITY.md](SECURITY.md)** — enforcement is via `.gitignore`, `.gitleaks.toml`,
pre-commit hooks, and an always-on secret-scan workflow.

---

## Payments & email — test vs. real

**Payments** run behind one seam (`IPaymentGateway`), selected by `Payments:Provider`:

- **Mock (default)** — no real charge, no external account. Approves normal tokens, declines
  a "decline" token, and treats BNPL/"klarna" tokens as an **asynchronous** authorization
  that a webhook settles. The whole card / Google Pay / Klarna checkout is demoable this way.
- **Stripe test mode** — set `Payments:Provider=Stripe` and a `sk_test_…` key (via secrets,
  never committed); pay with Stripe's **test cards** (`4242…` succeeds, `4000…0002` declines).
  No money moves. **Going live** is the same integration with your own **live** keys supplied
  through the secret mechanism above — `.gitleaks.toml` even blocks committing `sk_live_*`.

**Email** runs behind `IEmailSender`, selected by `Email:Provider`:

- **Dev (default)** — writes each message to the API log (stdout), so you can read the
  password-reset link locally with no mail server.
- **SMTP** — real delivery via any provider (SendGrid, Mailgun, SES, Postmark) or a local
  catcher (Mailpit/MailHog) for offline testing; the SMTP password comes from secrets.

**Step-by-step to test email / payments / Google locally and to configure each for real:**
the **[Runbook (ch. 9)](docs/handbook/09-runbook.md)**. Reference detail:
**[Payments](docs/handbook/05-payments.md)** and **[Configuration → Email](docs/handbook/04-configuration-and-2fa.md)**.

---

## Repository layout

```
src/
  WidgetWorks.Domain          entities, value types, domain rules (no dependencies)
  WidgetWorks.Application     use-case handlers, ports (interfaces), DTOs
  WidgetWorks.Infrastructure  Dapper repos, security, payments, email, migrations, seed
  WidgetWorks.WebApi          Minimal API endpoints, DI, auth wiring
tests/WidgetWorks.UnitTests   xUnit tests with in-memory fakes + FakeTimeProvider
web/                          React + TypeScript SPA (Vite)  — see web/README.md
scripts/                      smoke-test.ps1 and tooling
docs/                         handbook + architecture ADRs
Dockerfile.api, Dockerfile.web, docker-compose.yml
```

## License

See [`LICENSE`](LICENSE).
