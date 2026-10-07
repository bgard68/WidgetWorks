[← Handbook index](README.md) · [Project README](../../README.md)

# 7. Testing & the smoke test

Five layers, and all five are the gate — no deployment runs unless every one passes:

| Layer | What it proves | Needs |
|---|---|---|
| **Backend unit** (xUnit) | handler and domain logic | nothing |
| **Repository integration** (xUnit) | the SQL: reservations, constraints, cascades | PostgreSQL |
| **API** (xUnit + `WebApplicationFactory`) | the HTTP surface in process: routing, binding, JWT pipeline, authorization, throttling, health | PostgreSQL |
| **Frontend unit** (Vitest + Testing Library) | components render and behave | jsdom |
| **Smoke test** (PowerShell) | the running API over HTTP, end to end | Docker |

**Coverage floors: 95% backend (merged across the suites, `scripts/check-coverage.sh`) and 100%
frontend statements, branches, functions and lines (`vitest.config.ts`).** Both are enforced in
CI, so a regression fails the build. The frontend's 100% is honest because the few genuinely
unreachable guards are excluded at the site with a `v8 ignore` comment giving the reason. They
are floors, not targets: they catch a slide, they are not an invitation to
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
- Checkout idempotency — a repeated submit replays the first order (one order, one
  reservation, one receipt), a key still in flight is refused rather than charged twice, a
  key reused for a different body is refused, a declined attempt replays as the same
  decline, and key retention forgets what it should.
- Refunds — only a paid order, the provider asked before the order is written, one key across
  every attempt so a retry pays out once, an unconfirmed refund leaving the order untouched,
  and `Refunded` being unreachable through the generic status endpoint. Partial refunds
  accumulate, cannot exceed what is owed, keep the order `Paid`, and release stock only on
  the one that settles it.
- The audit trail — who refunded what, recorded after the change lands, including the
  refusals and the unknown outcomes, and a payout the order could not record being flagged
  for reconciling rather than reported as a plain failure.
- Staff review list — an unconfirmed charge is visible with how long it has been stuck; two
  identical orders minutes apart are flagged, the same order hours later is not, and a retry
  after a decline never is.
- Payment reconciliation — a charge the provider never confirmed parks the order with its
  stock held, survives an expiry sweep that would otherwise fail it, and is then settled
  from what the provider actually says: paid (with the receipt checkout could not send),
  not completed (stock released only now), still in progress (handed back to the webhook
  path), or still unknown — which changes nothing at all and escalates once it is old
  enough. Plus the Stripe adapter's side: one idempotency key across every retry, no retry
  on an answer Stripe actually gave, and a probe that searches rather than creating.
- Payments — async settlement (webhook → Paid / PaymentFailed, idempotent, unknown ref).
- Orders — lifecycle transitions (Paid→Shipped→Delivered / Cancelled).

Run them:

```bash
dotnet test tests/WidgetWorks.UnitTests
```

(A bare `dotnet test` from the repo root also runs the repository and API suites, which need
PostgreSQL — see below.) CI runs `dotnet build -warnaserror` then `dotnet test` on every code change (see below).

## Frontend unit tests

`web/src/**/*.test.{ts,tsx}` (Vitest) cover the logic that isn't worth a browser:

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
- **Refund concurrency** — two refunds racing on one order, both reading a zero running total and
  both trying to write the same new one: exactly one applies. Overpaying is prevented by the
  database, which is the only place that claim can be tested.
- **The reconciliation exemption** — an unconfirmed order is invisible to the stale-reservation
  query and visible to the reconciliation one, in real SQL. The whole design rests on that pair of
  queries disagreeing about the same row.
- **Atomic idempotency claims** — thirty-two concurrent claims on one key, exactly one
  winner. In the unit suite the ledger is a dictionary behind a lock, which proves the
  handler honours the contract but not that the contract holds; `on conflict do nothing`
  over real concurrent connections is the only place that promise is actually kept.

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

## API tests

`tests/WidgetWorks.ApiTests` boots the **real `Program`** in process with `WebApplicationFactory`,
against a throwaway PostgreSQL database created for the run (migrations and seeding included) and
dropped afterwards. The unit suite proves the handlers and the integration suite proves the SQL;
this suite proves the part neither can — the HTTP surface itself:

- **Auth, cart/checkout, catalog, orders, 2FA and webhooks** end to end over HTTP, including the
  JWT bearer pipeline with its security-stamp check and the Customer / Manager / Administrator
  policies.
- **Throttling** — the limits actually engage (`RateLimitingApiTests`), the client address used as
  the partition key (`ClientAddressTests`), and the startup warnings for a misconfigured proxy
  setting or a scaled-out instance (`ProxyConfigurationCheckTests`, `ScaleOutCheckTests`).
- **Operability** — `/health` vs `/health/ready`, correlation ids on 500s, and what the API does when
  its database is gone (`DiagnosticsApiTests`, `DatabaseOutageApiTests`).
- **Config and background work** — the shipped `appsettings.json` stays honest
  (`ShippedConfigurationTests`), and the reservation sweep's on/off switch (`ReservationSweeperTests`).
- **Checkout under duplicate load** (`CheckoutIdempotencyLoadTests`) — fifty simultaneous copies of
  one request through the real pipeline, and forty shoppers submitting three times each. The order
  count is read back **from the database**, because an API agreeing with itself proves nothing.
  These are correctness tests with contention, not a benchmark: elapsed times are printed for
  context and never asserted on, since a timing threshold on shared CI hardware fails for reasons
  unrelated to the code. For the record, removing the `Idempotency-Key` header from that fifty-copy
  burst produces **24 orders** instead of one — the suite has teeth.

Run them against the compose database, the same way as the repository tests:

```bash
docker compose up -d db
```

```bash
dotnet test tests/WidgetWorks.ApiTests
```

`WIDGETWORKS_TEST_DB` overrides the connection, as above.

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
  Passed: 59 / 59
  All checks passed.
```

## CI pipeline

| Workflow | Runs on | What |
|---|---|---|
| **Secret scan** | every push/PR (incl. docs) | gitleaks — never skipped |
| **CI** | code changes (docs/scripts ignored) | format check, `dotnet build -warnaserror`, `dotnet test` (all three .NET suites, against a PostgreSQL service) + the 95% coverage floor; dependency review (public) |
| **CodeQL** | code changes (public) | security-extended analysis |
| **Web CI** | `web/**` changes | `npm run build` (tsc + Vite) |
| **Smoke test** | code changes (docs ignored) | `docker compose up db api` → wait `/health` → run `smoke-test.ps1` |
| **Test suite** | called by both deploys | all five layers plus the coverage floor — see below |
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
| `integration` | repository tests, then the API tests, against a PostgreSQL **service container** |
| `coverage` | `needs: [backend, integration]` — merges the unit and integration/API reports, enforces the **95%** floor |
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
