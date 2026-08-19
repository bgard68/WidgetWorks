[← Handbook index](README.md) · [Project README](../../README.md)

# 7. Testing & the smoke test

Four layers, and all four are the gate — no deployment runs unless every one passes:

| Layer | What it proves | Needs |
|---|---|---|
| **Backend unit** (xUnit) | handler and domain logic | nothing |
| **Repository integration** (xUnit) | the SQL: reservations, constraints, cascades | PostgreSQL |
| **Frontend unit** (Vitest + Testing Library) | components render and behave | jsdom |
| **Smoke test** (PowerShell) | the running API over HTTP, end to end | Docker |

**Coverage: 95.5% backend (merged), 86% frontend statements / 89.5% lines.** Floors are
enforced in CI — 90% backend, and thresholds in `vitest.config.ts` — so a regression fails
the build. They are floors, not targets: they catch a slide, they are not an invitation to
write tests that move a number.

## Backend unit tests

`tests/WidgetWorks.UnitTests` (xUnit) run with in-memory fakes and `FakeTimeProvider`, so
they’re deterministic and need no database. Coverage includes:

- Auth & security — lockout after N failures + unlock window, “secure my account” stamp
  rotation + refresh revocation, JWT creation, `kid` key ring (active signs, old validates,
  revoked rejected), password reset (single-use, expiry, stamp rotation, protected-admin
  excluded), Google login (provision / link / unverified-refused).
- 2FA — TOTP verify, challenge login, recovery codes.
- Catalog — inventory invariants, immutable-admin guard, create/update/adjust handlers.
- Cart — cap-at-available, accumulate, update-to-zero, guest→user merge.
- Pricing — per-state tax (known / 0% / unknown), shipping tiers, quote pipeline.
- Checkout — success (pay + reserve + clear cart), decline (release + keep cart),
  async pending (park in AwaitingPayment), insufficient stock, validation.
- Payments — async settlement (webhook → Paid / PaymentFailed, idempotent, unknown ref).
- Orders — lifecycle transitions (Paid→Shipped→Delivered / Cancelled).

Run them:

```bash
dotnet test
```

CI runs `dotnet build -warnaserror` then `dotnet test` on every code change (see below).

## Frontend unit tests

`web/**/*.test.ts` (Vitest) cover the logic that isn't worth a browser:

**Logic**

- **`api/client.test.ts`** — the token-refresh contract. The important case is the
  regression test for bug #12: fire several concurrent requests that all get a `401`, and
  assert the client issues **exactly one** refresh. Refresh tokens rotate, so a second
  concurrent refresh replays a dead token and signs the user out — the test fails loudly if
  the single-flight guard is ever removed.
- **`lib/catalog.test.ts`** — catalog filtering/sorting behaviour.

**Components** (Testing Library, jsdom) — the screens where a silent break costs the most:

- **`ProtectedRoute`** — every combination of signed-in / staff-route / role, including the
  half-written session (refresh token, no role) that must not open an admin screen.
- **`AdminWidgetsPage`** — nothing is sent before the delete confirmation, cancelling sends
  nothing at all, and a Manager is never shown the control.
- **`CheckoutPage`** — totals come from the server and are re-fetched when the state or
  shipping method changes; the selected payment method is the token actually submitted; a
  decline leaves the shopper on the page with the reason.
- **`LoginPage`** — the 2FA branch stores no session until the code is verified, and a guest
  cart merges on the way in without a merge failure undoing an accepted sign-in.
- **`Layout`**, **`CartPage`**, **`AdminOrderPage`**, the storefront and account pages.

Run them:

```bash
cd web && npm test
```

```bash
cd web && npm run test:coverage
```

`npm run build` (tsc + Vite) runs alongside them in CI, so a type error fails the same gate.

> **jsdom does not implement `<dialog>`.** `showModal`/`close` are absent, so any component
> built on the native modal throws on mount. `src/test/setup.ts` supplies minimal versions.

## Repository integration tests

`tests/WidgetWorks.IntegrationTests` runs the Dapper repositories against a **real
PostgreSQL**. This layer exists because the repositories are mostly SQL, and an in-memory
fake would only prove the fake works:

- **Stock reservation.** Ten concurrent buyers, two units each, ten in stock — exactly five
  may win. Overselling is prevented by a conditional `UPDATE` inside a transaction, and
  nothing short of concurrent connections against a real server demonstrates that.
- **Transactional integrity** — a refused reservation rolls the order row back with it.
- **Constraints and indexes** — SKU uniqueness folded through `upper()`, the `ON CONFLICT`
  cart upsert, cascading deletes.
- **Idempotent startup** — migrations journaled, and a seeder that can run on every boot
  without duplicating an account or resetting a password someone changed.

It creates and drops a **throwaway database per run**, migrated by the same DbUp scripts the
app runs at startup, so it never touches developer or demo data. Point it at any Postgres:

```bash
docker compose up -d db
```

```bash
dotnet test tests/WidgetWorks.IntegrationTests
```

It defaults to the local compose database. Override with `WIDGETWORKS_TEST_DB` (a connection
string to the **`postgres`** maintenance database — the suite creates its own from there).

> **Why not Testcontainers?** It pulls `SSH.NET 2024.2.0`, which carries a known
> high-severity advisory, and this repo builds with NuGet audit as an error. Using the
> Postgres that compose and CI already provide costs one environment variable instead.

## End-to-end smoke test

`scripts/smoke-test.ps1` drives the **running API** over HTTP and checks real responses.
It covers: health & catalog; register / login / refresh / logout; **real TOTP 2FA**
(enroll → confirm → challenge login → recovery code); cart → quote → checkout (mock
success) → admin fulfillment (ship / deliver) → guest order lookup; **asynchronous payment**
(AwaitingPayment → webhook → Paid/PaymentFailed) with its 404/400/ack guardrails;
**Google sign-in with a fake credential (must 401)**; and failure conditions (404 / 401 /
403 / 400, payment decline, no-enumeration forgot-password).

### Run it locally

Start the stack, then:

```powershell
# PowerShell 7+
pwsh ./scripts/smoke-test.ps1 -BaseUrl http://localhost:8080

# or Windows PowerShell 5.1
powershell -File .\scripts\smoke-test.ps1 -BaseUrl http://localhost:8080
```

Parameters: `-BaseUrl` (default `http://localhost:8080`), `-AdminEmail`, `-AdminPassword`
(default the seeded demo admin), `-SkipTwoFactor` (skip the TOTP section).

It prints `[PASS]` / `[FAIL]` per check and **exits non-zero if anything failed**, so it’s
CI-friendly. It creates throwaway users / widgets / orders in the dev database — expected.

Sample:

```
== Auth: register, login, refresh, logout ==
  [PASS] register new customer returns 200
  [PASS] login returns 200 with tokens
  ...
== Summary ==
  Passed: 45 / 45
  All checks passed.
```

## CI pipeline

| Workflow | Runs on | What |
|---|---|---|
| **Secret scan** | every push/PR (incl. docs) | gitleaks — never skipped |
| **CI** | code changes (docs/scripts ignored) | `dotnet build -warnaserror` + `dotnet test`; dependency review (public) |
| **CodeQL** | code changes (public) | security-extended analysis |
| **Web CI** | `web/**` changes | `npm run build` (tsc + Vite) |
| **Smoke test** | code changes (docs ignored) | `docker compose up db api` → wait `/health` → run `smoke-test.ps1` |
| **Test suite** | called by both deploys | all four layers plus the coverage floor — see below |
| **Deploy API** | `main`, only for `src/**`, `tests/**`, `Dockerfile.api`, build files | `needs: tests` → publish Release → zip-deploy to App Service |
| **Deploy web** | `main`, only for `web/**` | `needs: tests` → build the SPA → Static Web Apps |

**Docs-only changes** (`**.md`, `docs/**`) skip CI / CodeQL / Web CI / Smoke — only the
secret scan runs — so writing documentation never triggers a build **or a deployment**. The
smoke workflow can also be run on demand from the Actions tab (`workflow_dispatch`).

### The deployment gate

`test-suite.yml` is a **reusable** workflow (`on: workflow_call`) with five jobs:

| Job | What it runs |
|---|---|
| `backend` | unit tests + coverage report |
| `integration` | repository tests against a PostgreSQL **service container** |
| `coverage` | `needs: [backend, integration]` — merges both reports, enforces the **90%** floor |
| `frontend` | Vitest with thresholds, then `tsc` + Vite build |
| `smoke` | compose up, wait for `/health`, run `smoke-test.ps1` |

The floor is a separate job because **neither suite reaches it alone**: the repositories are
only exercised by the integration tests and the handlers only by the unit tests. Each uploads
its cobertura report; the floor job merges them by taking the highest hit count per line.
Summing or averaging would understate the real figure, because a line covered by one suite is
missed by the other.

Both deploy workflows start with:

```yaml
jobs:
  tests:
    uses: ./.github/workflows/test-suite.yml
  deploy:
    needs: tests
```

so a failure in **any** job — including the coverage floor — stops the deploy before a single
artifact is uploaded.
The web deploy runs the API smoke test too, deliberately: a SPA is useless against a broken
API, so it isn't allowed to ship on frontend tests alone.

Triggers are **allowlists**, not ignore-lists — the API deploy fires only for paths that can
change the compiled API, the web deploy only for `web/**`. An API change never redeploys the
SPA, a web change never redeploys the API, and a docs change deploys nothing.
