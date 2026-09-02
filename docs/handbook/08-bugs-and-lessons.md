[← Handbook index](README.md) · [Project README](../../README.md)

# 8. Bugs & lessons learned

Real issues hit while building this, how each was found, fixed, and prevented.

Two phases shaped the list. Early on the build environment could not install the .NET SDK,
so **code was authored without a local compiler and CI acted as the compiler** — which makes
the discipline below load-bearing rather than optional. Later, with a local toolchain and a
real deployment, the failures shifted: shells mangling arguments, a platform restarting a
crashing container, an identity provider presenting a subject nobody documented. A third pass
went after test coverage, and turned up a different class again: global state that only
misbehaves outside the DI container, and two gates that reported success while doing nothing.
Entries 1–11 are from the first phase, 12–32 from the second, and 33–39 from the third.
A fourth pass — a security and operability review of the whole application — produced
40–45, and those have a common shape worth noticing: most are places where a value was
read, reasoned about, and written back, while something else changed it in between.
Entry 46 came later still, from re-reading code that had already shipped: a defect held
in place by a passing test that stated the misconception out loud. Entry 47 is not code
at all — a configuration file silently rejected for three weeks, during which every fix
written into it did nothing. Entry 48 came from comparing a running storefront against the
code that seeded it.

## Bugs

Each entry below is one defect: what went wrong, how it surfaced, why it happened, what
fixed it, and what it taught. These were a six-column table until the prose outgrew it —
the rightmost column, the lesson, was falling off the edge of the page, which is exactly
the column worth reading.

### 1 · `dotnet restore` failed: NU1903

**Found by** CI (restore step)

**Cause** Transitive `Microsoft.OpenApi 2.0.0` had advisory GHSA-v5pm-xwqc-g5wc

**Fix** Pin `Microsoft.OpenApi 2.7.5` in WebApi.csproj

**Lesson** Warnings-as-errors + Dependabot + dependency-review surface vulnerable transitives early

### 2 · Build failed after adding `kid` rotation

**Found by** CI (build step)

**Cause** Changed `JwtTokenService` constructor but a unit test still called the old 2-arg ctor

**Fix** Update the test to build the new `JwtKeyRing`

**Lesson** When you change a ctor/signature, update all call sites; CI compiles tests too, so it caught it

### 3 · A pricing test failed

**Found by** CI (test step)

**Cause** Test asserted the wrong shipping amount — a single line of qty 2 means `itemCount = 2`, so the per-item surcharge applied

**Fix** Recompute the expected value from the same rules the code uses

**Lesson** Derive expected values from the specification, not intuition

### 4 · gitleaks flagged a **test** JWT key

**Found by** local gitleaks run

**Cause** A throwaway signing key in a unit test looked like a secret

**Fix** Allowlist a `test-signing-key…` pattern and reuse that prefix in test keys

**Lesson** Give test secrets an obviously-fake, allow-listed shape

### 5 · Web CI failed in ~2s

**Found by** CI check-run status

**Cause** Repo policy requires actions **pinned to a full commit SHA**; `actions/setup-node@v4` (a tag) was rejected

**Fix** Drop `setup-node`; use the runner’s preinstalled Node with only the SHA-pinned checkout

**Lesson** Pin every action to a SHA; prefer preinstalled tooling

### 6 · Google / Stripe config “not binding”

**Found by** reading the compose/env wiring

**Cause** `.env.example` used keys (`Authentication__Google__ClientId`, `Stripe__*`) that didn’t match the sections the code binds (`Google:*`, `Payments:Stripe:*`)

**Fix** Realign `.env.example` to the real keys

**Lesson** Keep example config in lockstep with the binding code; the smoke test would also reveal a mis-wired provider

### 7 · `git push` rejected (non-fast-forward)

**Found by** pushing a branch

**Cause** `main` moved (a parallel edit to `ci.yml`) while the branch rewrote the same file

**Fix** `git merge -X ours origin/main` to keep the branch’s CI, then push

**Lesson** Rebase/merge before pushing; keep unrelated changes in separate PRs

### 8 · “localhost:3000 refused to connect”

**Found by** opening the browser

**Cause** The stack wasn’t running yet (build unfinished / not started)

**Fix** Wait for `docker compose ps` to show all services up before opening

**Lesson** Poll `/health` (the smoke pipeline does exactly this)

### 9 · Couldn’t create an “empty” initial commit remotely

**Found by** pushing via API

**Cause** The sandbox git proxy blocked pushes; an empty tree is invalid

**Fix** Create the repo’s first commit locally

**Lesson** Know your tooling’s limits; script the repeatable path

### 10 · Always-on gitleaks failed on a green repo

**Found by** CI (Secret scan)

**Cause** Splitting gitleaks into a full-history scan surfaced test JWT keys after an edit dropped the `test-signing-key` allowlist

**Fix** Restore the dropped allowlist + email rules in `.gitleaks.toml`

**Lesson** Reproduce the exact scan locally before changing scanner scope; a whole-history scan is stricter than a PR-diff one

### 11 · `dotnet run` ignored user-secrets (empty signing key)

**Found by** running the API on the host

**Cause** No `launchSettings.json`, so `dotnet run` started in **Production**, where user-secrets aren’t loaded; it also bound `:5000` not `:5080`

**Fix** Add `Properties/launchSettings.json` pinning Development + `http://localhost:5080`

**Lesson** Commit a run profile so `dotnet run` is deterministic; env vars always load, user-secrets only in Development

### 12 · Random sign-outs while browsing

**Found by** reported, then reproduced with a control test (5 concurrent calls → 3× 401)

**Cause** Every 401 started its **own** refresh. Refresh tokens rotate, so the first call consumed the token and the rest replayed a dead one and were signed out

**Fix** **Single-flight** the refresh in `api/client.ts`: concurrent callers await one shared in-flight promise

**Lesson** A vitest regression test fires N concurrent 401s and asserts exactly one refresh call

### 13 · Receipt emails showed raw markup / mojibake

**Found by** Mailpit

**Cause** `SmtpEmailSender` set the HTML as both `Body` and an alternate view, and left encoding at the default

**Fix** Plain text `Body`, HTML as a **single** `AlternateView`, UTF-8 throughout

**Lesson** Read the message in a real mail client (Mailpit), not just "no exception thrown"

### 14 · No email at all under Docker

**Found by** nothing arriving in Mailpit

**Cause** `docker-compose.yml` never passed the SMTP (and payment) keys into the `api` container, so the app fell back to the no-op sender

**Fix** Pass the credential keys through in compose

**Lesson** Compose env is config too — it drifts from `.env.example` unless checked

### 15 · A widget named `Widget & Co <Pro>` broke the email layout

**Found by** reviewing the templates

**Cause** Values were interpolated into HTML unescaped, and money used the current culture

**Fix** `WebUtility.HtmlEncode` every interpolation; `CultureInfo.InvariantCulture` for money

**Lesson** Treat an email template as untrusted output, exactly like a web page

### 16 · Container restart-looped on a free tier — burning quota

**Found by** Azure App Service logs

**Cause** DbUp **threw** at startup when the database was unreachable, so the host killed and restarted the process forever

**Fix** `MigrationRunner.TryRun` retries with backoff and returns an outcome; the app boots anyway and `/health` reports **503**

**Lesson** Never let a transient dependency crash startup where the platform bills restarts

### 17 · Deep links 404'd and Google sign-in was blocked on Static Web Apps

**Found by** opening `/store` directly on the deployed SPA

**Cause** No SPA fallback, and no CSP allowance for `accounts.google.com`

**Fix** `staticwebapp.config.json` with `navigationFallback` + a CSP that allows the Google endpoints and the API origin

**Lesson** Verify a deep link and a third-party script on the **hosted** build, not just the dev server

### 18 · That config file did nothing

**Found by** `sed` in the deploy script found no file

**Cause** It sat in `web/`; Vite only copies static files from `web/public/`

**Fix** Move it to `web/public/`

**Lesson** Know which files your bundler actually emits

### 19 · Staff could not find any order

**Found by** using the admin screen

**Cause** The page only looked an order up **by GUID**, and nobody has a GUID to hand

**Fix** Add `GET /admin/orders` and make the list the entry point

**Lesson** A screen that needs an id you can't obtain is unusable, however correct its API

### 20 · That new list showed `Items: 0` for every order

**Found by** opening the screen

**Cause** I "optimized" `GetRecentAsync` to skip loading item rows — but `OrderSummary` derives `itemCount` from them

**Fix** Load the item rows

**Lesson** Don't drop data a projection depends on; check the projection before trimming the query

### 21 · Google sign-in rendered an empty slot

**Found by** the login page

**Cause** `GoogleButton` returns `null` without a client id, and the id wasn't set for the deployed build

**Fix** Supply `VITE_GOOGLE_CLIENT_ID` at build time; CSP as in #17

**Lesson** A silently-null component looks identical to a broken one — prefer a visible fallback

### 22 · The selected payment method looked unselected

**Found by** user screenshot, forced dark mode

**Cause** `.optioncard:hover` and `.optioncard.on` both drew a blue border, so hovering the selected card erased the distinction

**Fix** Neutral hover; an opaque inset ring for selected

**Lesson** Test selection state under hover **and** forced dark mode

### 23 · Frontend tests failed to compile

**Found by** `npm test`

**Cause** Tests used `.at(-1)`, an ES2022 API, against the project's ES2020 target

**Fix** A small `last()` helper

**Lesson** Match test code to the project's target; don't move the target for a convenience API

### 24 · Stylesheet silently dropped a theme override

**Found by** reviewing the built CSS

**Cause** Bare custom properties were written directly inside `@media` blocks instead of a selector

**Fix** Wrap them in `:root{}`

**Lesson** Custom properties need a selector — a media block isn't one

### 25 · `az webapp config appsettings set` failed: `Jwt__SigningKey was unexpected at this time`

**Found by** provisioning

**Cause** The Windows `az` batch shim mis-parses the parentheses in `@Microsoft.KeyVault(...)`

**Fix** Write the settings to a temp JSON file and pass `--settings @file`

**Lesson** Never pass shell-significant characters as inline CLI args on Windows

### 26 · Re-running provisioning failed on an existing vault

**Found by** second run of `Provision.ps1`

**Cause** `az keyvault create` isn't idempotent

**Fix** Guard vault, plan and web app with a `show` check first

**Lesson** "Idempotent" has to be proven by running it twice

### 27 · `RandomNumberGenerator::Fill()` not found

**Found by** generating the signing key

**Cause** Windows PowerShell 5.1 is .NET **Framework**; `Fill` is .NET Core+

**Fix** `RandomNumberGenerator.Create().GetBytes()`

**Lesson** Target the PowerShell edition the user actually has, not the one you assume

### 28 · An `az` helper swallowed `-o json`

**Found by** provisioning output was wrong

**Cause** A helper parameter named `$Args` collided with PowerShell's automatic `$Args`, so `-o` bound to `-OutVariable`

**Fix** Drop the param block

**Lesson** `$Args`, `$Input`, `$Error` are reserved — never name a parameter after one

### 29 · `az role assignment create` built a scope of `C:/Program Files/Git/subscriptions/…`

**Found by** assigning Key Vault RBAC

**Cause** Git Bash (MSYS) rewrites leading-`/` arguments into Windows paths

**Fix** `MSYS_NO_PATHCONV=1`, or run it from PowerShell

**Lesson** Shell-specific argument mangling looks exactly like a broken tool — check the shell first

### 30 · Azure OIDC login failed with a valid federated credential

**Found by** deploy workflow

**Cause** GitHub presented an **immutable** subject (`repo:owner@id/repo@id:environment:production`), not the documented `repo:owner/repo:environment:name` form

**Fix** Add federated credentials matching the subject actually presented

**Lesson** Read the subject from the failing token/log rather than from the docs

### 31 · A pinned action didn't exist

**Found by** deploy workflow, immediately

**Cause** I pinned `Azure/static-web-apps-deploy` to a SHA I had invented

**Fix** Verify every pin against the GitHub API

**Lesson** A SHA pin is only safe if the SHA is real — resolve it, don't recall it

### 32 · Branch protection could never be satisfied

**Found by** enabling required checks

**Cause** The rule required a check named `ci.yml`, which no job publishes — and requiring **path-filtered** workflows deadlocks docs-only PRs (they never run, so they never report)

**Fix** Require only `Secret scan (gitleaks)`, the one check that runs unconditionally

**Lesson** A required check must be one that runs on **every** PR

### 33 · A repository read returned `null` for `tracking_number` and `order_number` while `status` and `email` were fine

**Found by** writing the first integration test

**Cause** Dapper's snake_case→PascalCase mapping is **global process state**, set inline inside `AddInfrastructure`. Anything constructing a repository outside the DI container never turned it on, so only single-word columns mapped

**Fix** `DapperConfiguration.Apply()` — explicit, idempotent, called by both the container and the test fixture

**Lesson** Global mutable configuration is a hidden dependency; give it a name and call it, don't bury it in a registration method

### 34 · Coverage collapsed to 39% the moment a runsettings file was added, and `OrderRepository` reported 2 tracked lines

**Found by** the number moved the wrong way after a change that should not have moved it

**Cause** `CompilerGeneratedAttribute` was in `ExcludeByAttribute`. Every `async` method compiles to a state machine carrying that attribute, so the exclusion silently removed almost the whole codebase from measurement

**Fix** Drop it; keep only `GeneratedCode`, `Obsolete` and `ExcludeFromCodeCoverage`, with a comment saying why

**Lesson** Treat a coverage jump as suspicious in **both** directions — a number that improves for an unexplained reason is telling you the measurement broke

### 35 · The coverage gate passed at a 99% floor on a 95% codebase

**Found by** testing the gate's failure path, not its success path

**Cause** `command -v python3` resolves on Windows to an **App Execution Alias** that prints an advert for the Store and exits 0, so the script's body never ran and the gate always "passed"

**Fix** Execute each candidate interpreter before accepting it

**Lesson** A gate that has never been seen to fail is not known to work; test the red path first

### 36 · `dotnet restore` failed the moment a test dependency was added

**Found by** CI-equivalent restore locally

**Cause** Testcontainers pulls `SSH.NET 2024.2.0`, which carries a known high-severity advisory, and the repo builds with NuGet audit as an error

**Fix** Use the PostgreSQL that compose and CI already provide, via a connection-string env var

**Lesson** The audit gate works; when it fires on a *convenience* dependency, take the plainer route rather than weakening the gate

### 37 · A challenge-token test failed against a correct implementation

**Found by** writing tests around `ValidateChallengeTokenAsync`

**Cause** Issuance uses the injected `TimeProvider` but `TokenValidationParameters` has no such hook, so lifetime is validated against the **system** clock. A token minted at a fixed past date is born expired

**Fix** Anchor those tests on real time and move the *issuing* clock to express age

**Lesson** "Inject time everywhere" holds only as far as the libraries let it; find the seams that don't take your clock

### 38 · An integration test asserted a uniqueness rule the schema does not have

**Found by** the test failed

**Cause** `ix_widgets_live_name` is a plain index for ordering the live set, not a unique one. Only SKU is unique (case-folded via `upper(sku)`)

**Fix** Assert the rule that exists, and add a test documenting that names are deliberately **not** unique

**Lesson** Read the migration, not your memory of it — a test that asserts an imagined constraint fails honestly, but the same assumption in code would not

### 39 · New frontend tests passed but `tsc` failed the build

**Found by** running `npm run build`, not just `npm test`

**Cause** `.at(-1)` again — the **same ES2022-against-ES2020 trap as row 23** — plus a `let x = null` only assigned inside a Promise executor, which TypeScript narrows to `never`

**Fix** Index arithmetic, and `let release!: () => void`

**Lesson** Vitest transpiles without type-checking, so a green test run says nothing about the build. Run the gate CI runs

### 40 · An admin renaming a widget could silently revert a live reservation

**Found by** reading the write path after the inventory work

**Cause** One `UpdateAsync` wrote **every** column from the caller's in-memory object, and three handlers used load-change-save. A reservation taken between the read and the save was overwritten — an oversell caused by an edit, with no attacker and no error

**Fix** Split the write path by intent — details, stock, archive — and move the stock arithmetic and its guards into the `UPDATE` itself

**Lesson** A repository method that writes every column turns every caller into a potential lost update. Write what you mean, not the whole row

### 41 · Two deliveries of one payment webhook released the same stock twice

**Found by** tracing the settlement path

**Cause** The handler guarded with read-check-write, which two concurrent deliveries both pass. The second compensation decremented `quantity_reserved` again and ate stock held by a *different* order

**Fix** Compare-and-set: each payment write names the statuses it may apply from and returns whether it won; only the winner compensates

**Lesson** Application-level status checks are courtesy. If two callers can race, the row has to be the arbiter

### 42 · Order numbers collided at a few thousand orders a day

**Found by** arithmetic, not a failure

**Cause** The suffix was 6 hex characters of the order's Guid — 24 bits, scoped to one day. `order_number` is uniquely indexed, so a collision was never a leak, but it rolled the placement back: a hard checkout failure

**Fix** Widen to 10 characters (40 bits) and pin the width with a test

**Lesson** Collisions arrive by the birthday bound, not when the space runs out. 16.7 million values is a coin flip at 5,000 a day

### 43 · The API test suite passed individually and failed together

**Found by** adding rate limiting

**Cause** The test server sends no remote address, so every request shared one throttling partition and the suite exhausted a realistic budget between its own tests

**Fix** Raise the budgets in the fixture explicitly, and prove rejection separately with a host configured down to two

**Lesson** The suite reproduced the exact production failure mode — all callers in one partition — which is the trap behind any reverse proxy

### 44 · The health endpoint could never report unhealthy

**Found by** asking an operations question, not a security one

**Cause** `/health` closed over a variable captured at **startup**, so once the process was up it answered `ok` forever — database gone, still `ok`. The keep-warm ping held it in rotation on an answer that could not change

**Fix** Add `/health/ready` that queries the database; leave `/health` shallow

**Lesson** The obvious fix — query the database in `/health` — would have cost ~180 CU-hrs against a 100 CU-hr budget. The shallow probe was deliberate and said so in a comment

### 45 · CodeQL found log injection in the code written to fix a logging gap

**Found by** the scanner blocked the merge

**Cause** The new exception handler logged `Request.Path.Value` raw. That is the **decoded** path, so `%0A` in a URL arrives as a real newline and the caller writes their own log entry (CWE-117)

**Fix** `LogSafe.Text` strips control characters from the path and method, keeping printable oddities

**Lesson** The correlation id on the line beside it *was* sanitised, with a comment citing this exact weakness. Knowing a rule is not the same as applying it everywhere it holds

### 46 · Rate limiting read the one part of the header the attacker controls

**Found by** review of the shipped code, after the throttling work was already merged and deployed

**Cause** `ClientAddress` took the **leftmost** entry of `X-Forwarded-For` as the client. A proxy *appends* to that header, it does not replace it, so a caller sending `X-Forwarded-For: 9.9.9.9` arrives as `9.9.9.9, <real client>` and position zero is whatever they typed. Varying it per request mints a fresh partition every time, which is throttling defeated — with `TrustForwardedFor` correctly set to `true`. A unit test asserted the wrong semantics in so many words (`The_leftmost_entry_in_a_forwarded_chain_is_the_client`), so the defect was pinned by a passing test that described the code accurately

**Fix** Count from the trusted end instead: with `TrustedProxyHops` proxies in front, the client is at `count - hops`, and everything to its left is caller-supplied and ignored. A chain shorter than the hop count falls back to the connection address rather than trusting an entry nearer the caller. Entries are normalised through `IPAddress`/`IPEndPoint`, which drops the `:port` App Service appends — keeping it would have partitioned per connection instead of per caller and reopened the same hole. `ProxyConfigurationCheck` gained a third warning for a hop count higher than the traffic

**Lesson** `TrustForwardedFor` was the setting everyone argued about, and getting it right bought less than it looked like. The trust boundary is not *whether* to read the header — it is *which byte range of it* a proxy actually wrote. A test can hold a misconception in place as firmly as it holds a behaviour, and this one read as a specification while documenting the bug

### 47 · A config file was rejected for three weeks and said nothing

**Found by** the same unmergeable pair of pull requests arriving four Mondays running. The tell was in Dependabot's own titles, which kept naming a group — `dotnet-minor-patch` — that this file had stopped declaring three weeks earlier

**Cause** `versioning-strategy`, `commit-message.prefix-development`, and a group `dependency-type` were added to the **NuGet** block on 2026-08-07. All three are documented for ecosystems that distinguish production from development dependencies — `bundler`, `composer`, `mix`, `maven`, `npm`, `pip` — and NuGet does not. One invalid key rejects the **whole file**, not the block containing it, and rejection is not a stop: Dependabot falls back to the last version that parsed and keeps running from it, reporting nothing. Every edit after that date was inert, including the `codeql-action` grouping added on 08-24 for the express purpose of stopping `init` and `analyze` arriving as two pull requests that each fail with `Loaded a configuration file for version X, but running version Y`. That fix was correct, and carried a comment naming the exact failure it prevented, and never ran once

**Fix** Dropped the three keys from the NuGet block — npm keeps them, npm has the split. Then `scripts/check-dependabot-config.py` and a weekly workflow that compares the group names this file declares against the ones Dependabot actually emits, failing when they disagree. Replayed against this incident it fires on the first Monday rather than the fourth

**Lesson** GitHub *does* fail the `.github/dependabot.yml` check on a pull request that introduces an invalid file, so the way in is guarded. What nothing watched was the state afterwards: that check only runs on pull requests touching the file, so once a bad config reaches `main` it is never re-examined. A file consumed by someone else's service has no compiler and no test — the only evidence it is live is the behaviour it is supposed to produce. The symptom was repaired by hand four times, and each time this file was re-read and pronounced fine, because reading it was never evidence


### 48 · A third of the catalog kept names the code had renamed twice

**Found by** reading the deployed storefront next to the seed that is supposed to fill it. The grid showed `Deluxe Widget Block` where `DbSeeder` says `Deluxe Widget Block Fuchsia`, the Fuchsia variants were missing from an otherwise alphabetical run, and `Widget Pro Kit` appeared twice. The product count was right — 75, exactly what the seed defines — which is why nothing had looked wrong

**Cause** `SeedWidgetsAsync` inserts a demo widget only when its SKU is absent and never updates one. That rule is correct on its own terms: a restart must not overwrite a name or price an administrator edited. What it also means is that changing the content of a SKU already in `DemoWidgets` never reaches a database that has it, and two rounds of renaming had been stranded that way. WW-006..WW-025 were one round behind, still missing the finish that now distinguishes three variants of each shape. WW-001..WW-005 were worse: they still held the five original products from the very first seed, and those five SKUs had since been reassigned to entirely different products, so name, description **and** price all described the wrong item — WW-003 offered a $12.99 Standard Widget Valve at $49.99, and WW-005 carried the name `Widget Pro Kit` already held by WW-021

**Fix** `0012_RealignDemoCatalog.sql` corrects the 25 rows by SKU, once, and only the fields the seeder owns — quantities are left alone, because stock moves with orders and forcing it back to a seed figure could put `quantity_on_hand` below `quantity_reserved` and fail `ck_widgets_reserved_range`. `SeedWidgetsAsync` now documents the rule it implies: adding a SKU is enough, changing an existing one also needs a migration. Two integration tests execute the shipped embedded script rather than a copy, one proving a stale row is corrected and one proving stock survives and a second run is a no-op

**Lesson** An insert-if-missing seed is a one-way door. After its first run the code stops being the source of truth for anything already inserted, and every later edit to that data is a statement about new databases only. The row count is what hides it: nothing is missing, nothing is duplicated, the totals reconcile against the seed perfectly — and totals are what gets checked. Content is not counted. The evidence that a seed is live is the *content* of the rows it claims to own, and the only way to see it was to read the running site against the code


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
- **Let the row arbitrate, not the handler.** Read-check-write reads like a guard and is not
  one: any two callers who can race will both pass it. Compare-and-set — naming the states a
  write may apply from and acting only when it wins — turned duplicate webhooks, out-of-order
  events and concurrent sweeps from bugs into no-ops, and it is one `where` clause.
- **Write what you mean, not the whole row.** A single repository method that set every
  column made every caller a possible lost update, including an admin editing a product name
  during a checkout. Splitting writes by intent removed a whole class of defect rather than
  one instance of it.
- **Read the reasoning before changing the code.** The obvious fix for a health check that
  could not fail was to make it query the database — which would have doubled the database
  bill, for reasons written down in the workflow that pings it. It would have passed every
  test and looked right in review.
- **The gates catch what the author cannot.** Three real defects in this round were found by
  CI, by the coverage floor, and by CodeQL — not by re-reading the diff. The one that stings
  is the last: log injection in a handler whose *neighbouring line* was sanitised against
  exactly that weakness.
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
- **A measurement that improves for no reason is broken.** Coverage jumping the wrong way
  after a settings change, and a floor "passing" when set above the actual figure, were both
  the instrument failing rather than the code improving. Test a gate's red path before
  trusting its green one.
- **Global mutable configuration is a hidden dependency.** Dapper's column mapping worked
  perfectly through the container and silently mis-mapped everything outside it. Naming it
  and calling it explicitly turned an invisible coupling into a one-line requirement.
- **A fake that lies is worse than no fake.** A no-op `TouchAsync` let handlers forget to
  stamp the cart and still pass. Fakes have to model the behaviour they stand in for, or the
  suite is decorative.
- **Some invariants only exist in the database.** Atomic stock reservation cannot be
  demonstrated by any in-memory double; it needs concurrent connections to a real server.
  Where the rule lives decides what kind of test can prove it.
- **Configuration a service consumes needs an output check, not a review.** A rejected
  `dependabot.yml` does not fail loudly — the service keeps running the last version that
  parsed. The file read correctly throughout, including a fix written for the very symptom
  being repaired by hand each week. Compare what the config *declares* against what the
  service *emits*, and fail when they disagree.
- **An insert-if-missing seed only speaks to empty databases.** Skipping a row that exists
  protects an operator's edits and freezes everything else. The count stays right while the
  content goes stale, so reconcile what the seed *says* against what the deployment *serves*,
  and carry corrections to existing rows in a migration.
