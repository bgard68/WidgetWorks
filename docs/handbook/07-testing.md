[← Handbook index](README.md) · [Project README](../../README.md)

# 7. Testing & the smoke test

Two layers: fast **unit tests** (logic, no I/O) and an **end-to-end smoke test**
(the running API over HTTP).

## Unit tests

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

**Docs-only changes** (`**.md`, `docs/**`) skip CI / CodeQL / Web CI / Smoke — only the
secret scan runs — so writing documentation never triggers a build. The smoke workflow can
also be run on demand from the Actions tab (`workflow_dispatch`).
