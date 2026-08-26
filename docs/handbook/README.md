[← Project README](../../README.md)

# WidgetWorks Handbook

A production-shaped, end-to-end online widget store built as a portfolio showcase:
**.NET 10 (Minimal API, Dapper, PostgreSQL) + React/TypeScript SPA**, with real auth,
2FA, token rotation, catalog/inventory, cart, per-state tax, checkout with pluggable
payments (sync + async/webhook), transactional email, and an order lifecycle.

## Contents

1. [Overview](01-overview.md) — what it is, features, tech stack, repo layout.
2. [Architecture](02-architecture.md) — onion/clean layering, request flow, security model, seams.
3. [Setup & run](03-setup-and-run.md) — one-command Docker, hybrid dev, URLs, demo accounts.
4. [Configuration, secrets, email & 2FA](04-configuration-and-2fa.md) — what keys go where/how/why; email + Google setup; how to set up 2FA.
5. [Payments, tax & testing credit cards](05-payments.md) — how the total is built (shipping + per-state sales tax, worked examples, the rate table), Mock + Stripe test mode, async/webhooks, testing without charging a card, going live.
6. [Database & schema](06-database.md) — why PostgreSQL, migrations, tables and relationships.
7. [Testing & smoke test](07-testing.md) — four layers (backend unit, PostgreSQL integration, frontend component, end-to-end smoke), the coverage floors CI enforces, and how to run each locally.
8. [Bugs & lessons learned](08-bugs-and-lessons.md) — 46 real bugs, how each was found, fixed, and prevented — from CI-as-compiler through deployment, test coverage, and a security and operability review.
9. [Runbook — testing & going live](09-runbook.md) — step-by-step to test email, payments, and Google locally, and exactly what to change to go live.
10. [Deploying to Azure on free tiers](10-deploy-azure-free.md) — the whole stack for $0: F1 App Service, Static Web Apps, Key Vault with a managed identity, and Postgres on Neon.

## Related docs

- [Project README](../../README.md) — quick start + the documentation hub.
- [Security policy](../../SECURITY.md) — what's never committed and how it's enforced.
- [Local development notes](../local-development.md) — deeper dev-workflow detail.
- [Web app README](../../web/README.md) — the React/TypeScript SPA.
- [Architecture ADRs](../architecture/) — decision records behind the design.
