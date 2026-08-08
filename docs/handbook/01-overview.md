# 1. Overview

**WidgetWorks** is an end-to-end online store that sells “widgets.” It is a portfolio
project built to a production security posture — the goal is to demonstrate the hard
parts most demos skip, on clean, testable, time-abstracted code.

## What it does

- **Storefront** — browse/search a catalog, product detail, cart (guest or signed-in).
- **Checkout** — server-side re-priced totals: subtotal → shipping → per-state US sales
  tax → total; guest checkout or registered; Mock or Stripe (test) payment.
- **Accounts** — register, login, JWT access + rotating refresh tokens, **TOTP 2FA** with
  recovery codes, **Google OIDC** sign-in, password reset, and “secure my account”
  (rotate a compromised user’s sessions instantly).
- **Admin/Manager** — manage widgets and inventory, and drive order status
  (Paid → Shipped → Delivered / Cancelled) with tracking; an **immutable seeded admin**
  so the demo always works.
- **Notifications** — real transactional email (order received / shipped / cancelled,
  registration, password reset).

## Tech stack

| Area | Choice |
|---|---|
| API | .NET 10, ASP.NET Core **Minimal API** |
| Data | **Dapper** (no EF Core) over **PostgreSQL 16**; **DbUp** SQL migrations |
| Arch | Onion / Clean — `Domain → Application → Infrastructure → WebApi` (no MediatR) |
| Auth | JWT (short-lived access + rotating refresh), per-user **security stamp**, `kid` key rotation, **TOTP 2FA** (Otp.NET), **Google OIDC** |
| Time | `TimeProvider` everywhere for deterministic, testable time |
| Payments | `IPaymentGateway` — Mock (default) + Stripe test mode |
| Web | **React 18 + TypeScript** (Vite) SPA |
| Run | **Docker Compose** (db + api + web) |
| CI | GitHub Actions — gitleaks, build (warnings-as-errors) + tests, CodeQL, Dependabot, web build |

## Repository layout

```
src/
  WidgetWorks.Domain          entities, value types, domain rules (no dependencies)
  WidgetWorks.Application     use-case handlers, ports (interfaces), DTOs
  WidgetWorks.Infrastructure  Dapper repos, security, payments, email, migrations, seed
  WidgetWorks.WebApi          Minimal API endpoints, DI, auth wiring
tests/
  WidgetWorks.UnitTests       xUnit tests with in-memory fakes + FakeTimeProvider
web/                          React + TypeScript SPA (Vite)
scripts/                      smoke-test.ps1 and tooling
docs/                         handbook (this) + architecture ADRs
Dockerfile.api, Dockerfile.web, docker-compose.yml
```
