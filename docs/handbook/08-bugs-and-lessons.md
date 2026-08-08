[← Handbook index](README.md) · [Project README](../../README.md)

# 8. Bugs & lessons learned

Real issues hit while building this, how each was found, fixed, and prevented. A recurring
constraint shaped the workflow: the build environment could not install the .NET SDK, so
**code was authored without a local compiler and CI acted as the compiler** — which makes
the discipline below load-bearing rather than optional.

## Bugs

| # | Symptom | How found | Root cause | Fix | Prevention |
|---|---|---|---|---|---|
| 1 | `dotnet restore` failed: NU1903 | CI (restore step) | Transitive `Microsoft.OpenApi 2.0.0` had advisory GHSA-v5pm-xwqc-g5wc | Pin `Microsoft.OpenApi 2.7.5` in WebApi.csproj | Warnings-as-errors + Dependabot + dependency-review surface vulnerable transitives early |
| 2 | Build failed after adding `kid` rotation | CI (build step) | Changed `JwtTokenService` constructor but a unit test still called the old 2-arg ctor | Update the test to build the new `JwtKeyRing` | When you change a ctor/signature, update all call sites; CI compiles tests too, so it caught it |
| 3 | A pricing test failed | CI (test step) | Test asserted the wrong shipping amount — a single line of qty 2 means `itemCount = 2`, so the per-item surcharge applied | Recompute the expected value from the same rules the code uses | Derive expected values from the specification, not intuition |
| 4 | gitleaks flagged a **test** JWT key | local gitleaks run | A throwaway signing key in a unit test looked like a secret | Allowlist a `test-signing-key…` pattern and reuse that prefix in test keys | Give test secrets an obviously-fake, allow-listed shape |
| 5 | Web CI failed in ~2s | CI check-run status | Repo policy requires actions **pinned to a full commit SHA**; `actions/setup-node@v4` (a tag) was rejected | Drop `setup-node`; use the runner’s preinstalled Node with only the SHA-pinned checkout | Pin every action to a SHA; prefer preinstalled tooling |
| 6 | Google / Stripe config “not binding” | reading the compose/env wiring | `.env.example` used keys (`Authentication__Google__ClientId`, `Stripe__*`) that didn’t match the sections the code binds (`Google:*`, `Payments:Stripe:*`) | Realign `.env.example` to the real keys | Keep example config in lockstep with the binding code; the smoke test would also reveal a mis-wired provider |
| 7 | `git push` rejected (non-fast-forward) | pushing a branch | `main` moved (a parallel edit to `ci.yml`) while the branch rewrote the same file | `git merge -X ours origin/main` to keep the branch’s CI, then push | Rebase/merge before pushing; keep unrelated changes in separate PRs |
| 8 | “localhost:3000 refused to connect” | opening the browser | The stack wasn’t running yet (build unfinished / not started) | Wait for `docker compose ps` to show all services up before opening | Poll `/health` (the smoke pipeline does exactly this) |
| 9 | Couldn’t create an “empty” initial commit remotely | pushing via API | The sandbox git proxy blocked pushes; an empty tree is invalid | Create the repo’s first commit locally | Know your tooling’s limits; script the repeatable path |
| 10 | Always-on gitleaks failed on a green repo | CI (Secret scan) | Splitting gitleaks into a full-history scan surfaced test JWT keys after an edit dropped the `test-signing-key` allowlist | Restore the dropped allowlist + email rules in `.gitleaks.toml` | Reproduce the exact scan locally before changing scanner scope; a whole-history scan is stricter than a PR-diff one |
| 11 | `dotnet run` ignored user-secrets (empty signing key) | running the API on the host | No `launchSettings.json`, so `dotnet run` started in **Production**, where user-secrets aren’t loaded; it also bound `:5000` not `:5080` | Add `Properties/launchSettings.json` pinning Development + `http://localhost:5080` | Commit a run profile so `dotnet run` is deterministic; env vars always load, user-secrets only in Development |

## Lessons learned

- **Treat CI as the compiler.** With no local SDK, `build -warnaserror` + `test` on every
  push was the safety net. Warnings-as-errors turns latent problems (unused code,
  nullability, vulnerable packages) into hard failures you can’t merge past.
- **Program to seams (ports & adapters).** Payments, tax, shipping, email, and Google auth
  are interfaces with swappable implementations. It kept `CheckoutHandler` stable while
  Mock↔Stripe, table↔tax-service, and Dev↔SMTP were interchangeable — and made everything
  unit-testable. The async payment path (AwaitingPayment → webhook) later slotted in behind
  the same seam without touching checkout’s core.
- **Inject time.** `TimeProvider` everywhere made lockout windows, token expiry, and TOTP
  deterministic in tests instead of `Thread.Sleep`-flaky.
- **Defense in depth for invariants.** The immutable admin is enforced in the domain
  **and** by a database trigger — a bug or a direct SQL write still can’t violate it.
- **Never trust the client for money.** Totals — subtotal, shipping, and per-state tax — are
  recomputed server-side at checkout; the browser’s numbers are display-only.
- **Secrets discipline pays off.** `.gitignore` + `.gitleaks.toml` + an always-on secret
  scan meant “no secret in the repo” was enforced, not aspirational — and the one allowed
  exception (documented demo creds) is explicit.
- **Idempotent, deterministic seeds & migrations.** Seeds insert-if-absent; migrations are
  journaled and run once. Startup is safe to repeat.
- **Soft-delete over hard-delete** for catalog: hiding a widget (`is_active=false`) keeps
  order history intact.
- **Config must match the code that binds it, and the run profile must match the docs.** A
  mismatched example key — or a missing `launchSettings.json` — is a silent
  misconfiguration; keep them in lockstep and smoke-test the wired providers.
