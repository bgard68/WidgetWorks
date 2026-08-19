[← Handbook index](README.md) · [Project README](../../README.md)

# 1. Overview

**WidgetWorks** is an end-to-end online store that sells “widgets.” It is a portfolio
project built to a production security posture — the goal is to demonstrate the hard
parts most demos skip, on clean, testable, time-abstracted code.

## What it does

- **Landing guide** — everyone arrives at `/`, a plain-language page that explains the demo,
  states up front that **no payment is ever taken**, hands out the three demo accounts with
  what each role can and can't do, and links into the store at `/store`.
- **Storefront** — browse/search a catalog, product detail, cart (guest or signed-in).
- **Checkout** — server-side re-priced totals: subtotal → shipping → per-state US sales
  tax → total; guest checkout or registered; Mock or Stripe (test) payment, including an
  asynchronous BNPL path settled by webhook.
- **Accounts** — register, login, JWT access + rotating refresh tokens, **TOTP 2FA** with
  recovery codes, **Google OIDC** sign-in, password reset, and “secure my account”
  (rotate a compromised user’s sessions instantly).
- **Three roles** — **Customer** (shop, own order history), **Manager** (catalog + order
  fulfilment), **Administrator** (everything, plus retiring a widget and managing users).
  All three are seeded, so every policy in the app is exercisable from the login screen.
- **Admin/Manager** — manage widgets and inventory, browse recent orders and drive their
  status (Paid → Shipped → Delivered / Cancelled) with tracking. An Administrator can also
  **retire** a widget: deleted outright if it was never ordered, archived if it appears on
  one, so order history stays intact. An **immutable seeded admin** keeps the demo working.
- **Notifications** — real transactional email (order received / shipped / cancelled,
  registration, password reset), caught locally by Mailpit.

## Tech stack

| Area | Choice |
|---|---|
| API | .NET 10, ASP.NET Core **Minimal API** |
| Data | **Dapper** (no EF Core) over **PostgreSQL 16**; **DbUp** SQL migrations |
| Arch | Onion / Clean — `Domain → Application → Infrastructure → WebApi` (no MediatR) |
| Auth | JWT (short-lived access + rotating refresh), per-user **security stamp**, `kid` key rotation, **TOTP 2FA** (Otp.NET), **Google OIDC** |
| Time | `TimeProvider` everywhere for deterministic, testable time |
| Payments | `IPaymentGateway` — Mock (default) + Stripe test mode |
| Web | **React 18 + TypeScript** (Vite 8) SPA; **Vitest + Testing Library** |
| Run | **Docker Compose** (db + api + web + **Mailpit** mail catcher) |
| CI | GitHub Actions — gitleaks, build (warnings-as-errors) + tests, CodeQL, Dependabot, web build |
| Tests | 463 across four layers — backend unit, PostgreSQL integration, frontend component, end-to-end smoke. **95.5% backend / 89.5% frontend** lines, floors enforced in CI |
| CD | Path-scoped deploys (API and web move independently; docs move nothing), each gated on the **whole** test suite |
| Hosting | Azure **App Service F1** (API) + **Static Web Apps** (SPA) + **Key Vault** via managed identity, Postgres on **Neon** — all free tiers ([ch.10](10-deploy-azure-free.md)) |

## Repository layout

```
src/
  WidgetWorks.Domain          entities, value types, domain rules (no dependencies)
  WidgetWorks.Application     use-case handlers, ports (interfaces), DTOs
  WidgetWorks.Infrastructure  Dapper repos, security, payments, email, migrations, seed
  WidgetWorks.WebApi          Minimal API endpoints, DI, auth wiring
tests/
  WidgetWorks.UnitTests       xUnit tests with in-memory fakes + FakeTimeProvider
  WidgetWorks.IntegrationTests repository tests against a real PostgreSQL
web/                          React + TypeScript SPA (Vite)
infra/                        Provision.ps1 — idempotent Azure provisioning
scripts/                      smoke-test.ps1, deploy helpers, tooling
.github/workflows/            CI, path-scoped deploys, the reusable test suite
docs/                         handbook (this) + architecture ADRs
Dockerfile.api, Dockerfile.web, docker-compose.yml
```
