[← Project README](../README.md) · [Handbook](../docs/handbook/README.md)

# WidgetWorks Web (SPA)

The **React 18 + TypeScript** storefront, built with **Vite**. It talks to the WidgetWorks
API for catalog, cart, checkout, orders, auth (JWT + 2FA + Google), and admin.

## Run it

The easiest way to run the whole stack (web + API + Postgres) is Docker from the repo root —
see the **[project README](../README.md)** and **[Setup & run](../docs/handbook/03-setup-and-run.md)**.

To run just the SPA against an already-running API:

```bash
cd web
cp .env.example .env.local     # then set VITE_API_BASE_URL to your API's URL
npm install
npm run dev                    # http://localhost:5173
```

```bash
npm run build                  # tsc type-check + production build to dist/
```

## Configuration (build-time, no secrets committed)

Vite exposes only `VITE_*` variables, injected at **build time** from a git-ignored
`web/.env.local` (dev) or **GitHub Actions Variables** (CI — see `.github/workflows/web-ci.yml`).
Defaults live in [`src/lib/env.ts`](src/lib/env.ts).

| Variable | Purpose | Default |
|---|---|---|
| `VITE_API_BASE_URL` | Base URL of the API | `http://localhost:5080` |
| `VITE_GOOGLE_CLIENT_ID` | Google OAuth **client id** (public) | empty |

`VITE_GOOGLE_CLIENT_ID` is the **public** Google OAuth client id (safe in the browser bundle);
there is no client secret in this flow. Full policy — why these are build-time and never
committed — is in **[Configuration & secrets](../docs/handbook/04-configuration-and-2fa.md)**.

## Structure

```
src/
  api/        typed API client + response types
  auth/       auth context (token handling, refresh)
  cart/       cart context
  components/ layout (header, category nav, cart, footer)
  pages/      catalog, product, cart, checkout, order confirmation, orders, admin, auth
  lib/        env config, formatting, product-image helpers
  styles.css  global stylesheet
```

## Related docs

- [Project README](../README.md) — the documentation hub.
- [Architecture](../docs/handbook/02-architecture.md) — how the SPA fits the system.
- [Payments](../docs/handbook/05-payments.md) — the checkout payment methods (card / Google Pay / Klarna) and the async settlement the confirmation page uses.
