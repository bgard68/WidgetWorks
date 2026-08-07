# WidgetWorks

An end-to-end online **widget store** — a portfolio showcase built to a
production security posture. Not modeled on Amazon; the goal is to demonstrate
the hard parts most demos skip (real auth, 2FA, token rotation on compromise, an
immutable demo admin, catalog/inventory, checkout with pluggable payments, and
shipping) on clean, testable, time-abstracted code.

> Pre-planning stage. Full product & architecture specs live in
> [`docs/architecture/`](docs/architecture/).

## Tech stack (approved)

- **.NET 10** (LTS) / C# 14 — ASP.NET Core Minimal API
- **Dapper** over PostgreSQL 16 (no EF Core), **DbUp** SQL migrations
- **React + TypeScript** (Vite) SPA
- **Onion / Clean Architecture** — `Domain → Application → Infrastructure → WebApi` (no MediatR)
- **JWT** access + rotating refresh tokens, per-user **security stamp**, `kid`-based key rotation
- **TOTP 2FA** (Otp.NET) + recovery codes, **Google OIDC** sign-in
- **Payments:** mock gateway (default) + **Stripe test mode**, both behind `IPaymentGateway`
- **`TimeProvider`** everywhere for deterministic, testable time
- **Docker Compose** (api + db + web) for one-command run

## Repository layout (planned)

```
src/   WidgetWorks.Domain | .Application | .Infrastructure | .WebApi + .Web (React)
tests/ WidgetWorks.UnitTests | .IntegrationTests
docs/  architecture (PRD + technical design)
scripts/  infra & tooling (e.g. get-azure-infra.sh — pulls dynamically, no secrets)
infra/    IaC (exports are git-ignored)
```

## Security posture

No secrets, tokens, keys, cloud exports, logs, or AI/agent artifacts are
committed — enforced by `.gitignore`, `.gitleaks.toml`, pre-commit hooks, and CI
gates. **CodeQL** and **Dependabot** are enabled. The only sanctioned
"credentials" in the repo are the documented, throwaway **demo accounts**. See
[`SECURITY.md`](SECURITY.md).

### Demo accounts (local demo only)

| Role | Email | Notes |
|------|-------|-------|
| Admin (immutable) | `admin@widgetworks.demo` | Manages widgets, inventory, orders. Cannot be changed or deleted. |
| Customer | `demo@widgetworks.demo` | Standard shopper flow. |

Passwords are set from `.env` (see `.env.example`) at seed time.

## Getting started (once scaffolded)

```bash
cp .env.example .env      # fill in local values (never commit .env)
docker compose up         # api + postgres + web
```

## License

See [`LICENSE`](LICENSE).
