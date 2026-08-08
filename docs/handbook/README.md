# WidgetWorks Handbook

A production-shaped, end-to-end online widget store built as a portfolio showcase:
**.NET 10 (Minimal API, Dapper, PostgreSQL) + React/TypeScript SPA**, with real auth,
2FA, token rotation, catalog/inventory, cart, per-state tax, checkout with pluggable
payments, transactional email, and an order lifecycle.

## Contents

1. [Overview](01-overview.md) — what it is, features, tech stack, repo layout.
2. [Architecture](02-architecture.md) — onion/clean layering, request flow, security model, seams.
3. [Setup & run](03-setup-and-run.md) — one-command Docker, hybrid dev, URLs, demo accounts.
4. [Configuration, secrets, email & 2FA](04-configuration-and-2fa.md) — what keys go where/how/why; email + Google setup; how to set up 2FA.
5. [Payments & testing credit cards](05-payments.md) — Mock + Stripe test mode, how checkout charges, test cards.
6. [Database & schema](06-database.md) — why PostgreSQL, migrations, tables and relationships.
7. [Testing & smoke test](07-testing.md) — unit tests, CI gates, and how to run the end-to-end smoke test.
8. [Bugs & lessons learned](08-bugs-and-lessons.md) — real bugs hit, how found, how fixed, how prevented.

> Architecture decision records (ADRs) live alongside these docs in [`docs/architecture/`](../architecture/).
