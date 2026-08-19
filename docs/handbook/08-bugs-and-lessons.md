[← Handbook index](README.md) · [Project README](../../README.md)

# 8. Bugs & lessons learned

Real issues hit while building this, how each was found, fixed, and prevented.

Two phases shaped the list. Early on the build environment could not install the .NET SDK,
so **code was authored without a local compiler and CI acted as the compiler** — which makes
the discipline below load-bearing rather than optional. Later, with a local toolchain and a
real deployment, the failures shifted: shells mangling arguments, a platform restarting a
crashing container, an identity provider presenting a subject nobody documented. Rows 1–11
are from the first phase, 12–32 from the second.

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
| 12 | Random sign-outs while browsing | reported, then reproduced with a control test (5 concurrent calls → 3× 401) | Every 401 started its **own** refresh. Refresh tokens rotate, so the first call consumed the token and the rest replayed a dead one and were signed out | **Single-flight** the refresh in `api/client.ts`: concurrent callers await one shared in-flight promise | A vitest regression test fires N concurrent 401s and asserts exactly one refresh call |
| 13 | Receipt emails showed raw markup / mojibake | Mailpit | `SmtpEmailSender` set the HTML as both `Body` and an alternate view, and left encoding at the default | Plain text `Body`, HTML as a **single** `AlternateView`, UTF-8 throughout | Read the message in a real mail client (Mailpit), not just "no exception thrown" |
| 14 | No email at all under Docker | nothing arriving in Mailpit | `docker-compose.yml` never passed the SMTP (and payment) keys into the `api` container, so the app fell back to the no-op sender | Pass the credential keys through in compose | Compose env is config too — it drifts from `.env.example` unless checked |
| 15 | A widget named `Widget & Co <Pro>` broke the email layout | reviewing the templates | Values were interpolated into HTML unescaped, and money used the current culture | `WebUtility.HtmlEncode` every interpolation; `CultureInfo.InvariantCulture` for money | Treat an email template as untrusted output, exactly like a web page |
| 16 | Container restart-looped on a free tier — burning quota | Azure App Service logs | DbUp **threw** at startup when the database was unreachable, so the host killed and restarted the process forever | `MigrationRunner.TryRun` retries with backoff and returns an outcome; the app boots anyway and `/health` reports **503** | Never let a transient dependency crash startup where the platform bills restarts |
| 17 | Deep links 404'd and Google sign-in was blocked on Static Web Apps | opening `/store` directly on the deployed SPA | No SPA fallback, and no CSP allowance for `accounts.google.com` | `staticwebapp.config.json` with `navigationFallback` + a CSP that allows the Google endpoints and the API origin | Verify a deep link and a third-party script on the **hosted** build, not just the dev server |
| 18 | That config file did nothing | `sed` in the deploy script found no file | It sat in `web/`; Vite only copies static files from `web/public/` | Move it to `web/public/` | Know which files your bundler actually emits |
| 19 | Staff could not find any order | using the admin screen | The page only looked an order up **by GUID**, and nobody has a GUID to hand | Add `GET /admin/orders` and make the list the entry point | A screen that needs an id you can't obtain is unusable, however correct its API |
| 20 | That new list showed `Items: 0` for every order | opening the screen | I "optimized" `GetRecentAsync` to skip loading item rows — but `OrderSummary` derives `itemCount` from them | Load the item rows | Don't drop data a projection depends on; check the projection before trimming the query |
| 21 | Google sign-in rendered an empty slot | the login page | `GoogleButton` returns `null` without a client id, and the id wasn't set for the deployed build | Supply `VITE_GOOGLE_CLIENT_ID` at build time; CSP as in #17 | A silently-null component looks identical to a broken one — prefer a visible fallback |
| 22 | The selected payment method looked unselected | user screenshot, forced dark mode | `.optioncard:hover` and `.optioncard.on` both drew a blue border, so hovering the selected card erased the distinction | Neutral hover; an opaque inset ring for selected | Test selection state under hover **and** forced dark mode |
| 23 | Frontend tests failed to compile | `npm test` | Tests used `.at(-1)`, an ES2022 API, against the project's ES2020 target | A small `last()` helper | Match test code to the project's target; don't move the target for a convenience API |
| 24 | Stylesheet silently dropped a theme override | reviewing the built CSS | Bare custom properties were written directly inside `@media` blocks instead of a selector | Wrap them in `:root{}` | Custom properties need a selector — a media block isn't one |
| 25 | `az webapp config appsettings set` failed: `Jwt__SigningKey was unexpected at this time` | provisioning | The Windows `az` batch shim mis-parses the parentheses in `@Microsoft.KeyVault(...)` | Write the settings to a temp JSON file and pass `--settings @file` | Never pass shell-significant characters as inline CLI args on Windows |
| 26 | Re-running provisioning failed on an existing vault | second run of `Provision.ps1` | `az keyvault create` isn't idempotent | Guard vault, plan and web app with a `show` check first | "Idempotent" has to be proven by running it twice |
| 27 | `RandomNumberGenerator::Fill()` not found | generating the signing key | Windows PowerShell 5.1 is .NET **Framework**; `Fill` is .NET Core+ | `RandomNumberGenerator.Create().GetBytes()` | Target the PowerShell edition the user actually has, not the one you assume |
| 28 | An `az` helper swallowed `-o json` | provisioning output was wrong | A helper parameter named `$Args` collided with PowerShell's automatic `$Args`, so `-o` bound to `-OutVariable` | Drop the param block | `$Args`, `$Input`, `$Error` are reserved — never name a parameter after one |
| 29 | `az role assignment create` built a scope of `C:/Program Files/Git/subscriptions/…` | assigning Key Vault RBAC | Git Bash (MSYS) rewrites leading-`/` arguments into Windows paths | `MSYS_NO_PATHCONV=1`, or run it from PowerShell | Shell-specific argument mangling looks exactly like a broken tool — check the shell first |
| 30 | Azure OIDC login failed with a valid federated credential | deploy workflow | GitHub presented an **immutable** subject (`repo:owner@id/repo@id:environment:production`), not the documented `repo:owner/repo:environment:name` form | Add federated credentials matching the subject actually presented | Read the subject from the failing token/log rather than from the docs |
| 31 | A pinned action didn't exist | deploy workflow, immediately | I pinned `Azure/static-web-apps-deploy` to a SHA I had invented | Verify every pin against the GitHub API | A SHA pin is only safe if the SHA is real — resolve it, don't recall it |
| 32 | Branch protection could never be satisfied | enabling required checks | The rule required a check named `ci.yml`, which no job publishes — and requiring **path-filtered** workflows deadlocks docs-only PRs (they never run, so they never report) | Require only `Secret scan (gitleaks)`, the one check that runs unconditionally | A required check must be one that runs on **every** PR |

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
- **Single-flight shared async work.** Anything that *rotates* a credential — a refresh
  token, a nonce, a lease — can only be done once at a time. N callers must await one
  in-flight promise, not start N races. The bug looked like flaky auth for weeks.
- **Fail soft at startup when the platform bills restarts.** A free tier gives you a quota,
  and a crash loop spends it in minutes. Boot degraded, report it on `/health`, and let an
  operator see a 503 instead of an invisible restart cycle.
- **Hosting a SPA is its own configuration.** Client-side routes 404 without an explicit
  fallback, and a third-party sign-in button dies silently without CSP allowances. Neither
  shows up on a dev server — only on the hosted build.
- **Generate deployment config; never transcribe it.** The provisioning script reads back
  every id, hostname, and key it needs. Every value a human copies between two consoles is a
  future outage.
- **Shell-quoting is a platform detail, not a nuisance.** The same command breaks three
  different ways: a Windows batch shim mis-parsing parentheses, MSYS rewriting `/scopes/…`
  into `C:/Program Files/…`, and PowerShell binding `-o` to `-OutVariable` because a
  parameter was named `$Args`. When a CLI "is broken," suspect the shell first.
- **Verify pins and subjects against the source of truth.** A pinned action SHA that doesn't
  exist, and a federated-credential subject that doesn't match what the provider actually
  presents, both fail identically to a permissions problem. Resolve the real value — from the
  API, or from the failing token — instead of trusting documentation or memory.
- **A required status check must run on every PR.** Gating on a path-filtered workflow
  deadlocks any PR that doesn't touch those paths: it never runs, so it never reports.
- **Don't trim data a projection depends on.** Skipping the item rows made the query cheaper
  and every order display `0 items`. Read the mapper before optimizing the query.
