# WidgetWorks — Scope Decisions (resolves the open questions)

**Date:** August 7, 2026
**Status:** Accepted
**Supersedes:** the "open questions" in `01-Product-Requirements-Document.md` §11 and
`02-Architecture-and-Technical-Design.md` §16.

This records the resolutions to the six open scope questions and the design
implications of each. New ADRs 018–021 are added at the end.

---

## Q1 — Roles → **Add a "Manager" role** ✅

Three roles, not two:

| Role | Editable? | Can do | Cannot do |
|---|---|---|---|
| **Customer** | n/a | Shop, checkout, own orders, manage own profile/2FA | Any admin function |
| **Manager** | **Yes** (created/edited by an Administrator) | Manage widgets, inventory, and orders | Manage users/roles; view or touch the protected admin; change global security settings |
| **Administrator** | **No — immutable seeded super-admin** | Everything, incl. create/edit/disable Managers | Be modified or deleted (protected by domain + data guards) |

- Authorization uses **role- + policy-based** checks (ASP.NET Core policies), so
  "manage catalog" is a policy both Manager and Administrator satisfy, while
  "manage users" is Administrator-only.
- The immutable super-admin guarantee is unchanged (see arch §6.5). Managers are
  ordinary editable accounts — good for demonstrating real RBAC and an admin
  managing other admins.

---

## Q3 — Tax → **Calculate US sales tax per destination state** ✅

- A `ITaxCalculator` computes tax from the **shipping destination state** using a
  maintained **state-level rate table** (state → base sales-tax rate). Tax is
  stored on the order (`orders.tax`) and shown in the order review before payment.
- **Senior caveat (documented on purpose):** real US sales tax is *destination-based*
  for most states and has **thousands** of local/county/city jurisdictions plus
  product-category exemptions and economic-nexus rules. Modeling all of that is out
  of scope for a portfolio. v1 therefore uses a **state-level approximation**,
  clearly labeled as such, behind the `ITaxCalculator` seam so a real engine
  (**Avalara / TaxJar / Stripe Tax**) drops in later with zero checkout changes —
  the same Ports & Adapters move we use for payments.
- States with no state sales tax (e.g., a 0% state) yield $0 tax correctly.

---

## Q5 — Checkout → **Guest checkout AND registered login** ✅

- **Guests can check out** with just an email + shipping address (no account) — the
  low-friction path most stores offer.
- **Registered users log in** to get **order history/tracking**, saved addresses, and
  the security features (2FA, "secure my account").
- After a guest order, the confirmation offers **"create an account to track this
  order"**, which attaches the just-placed order to the new account by matching the
  order email. Guests can also look up an order by **order number + email**.
- Order-confirmation email is sent to guests and registered users alike (see Q8).

---

## Q4 — Coupons / promotions → **Out of v1** ✅

Left out of the first release; recorded as future work. The order-total pipeline
(subtotal → shipping → tax → total) is structured so a discount step can slot in
later without rework.

---

## Q8 — Email & 2FA → **Real, not mocked** ✅

We build these to behave like a real site, not a stub.

**Email — real transactional delivery.**
- `IEmailSender` is backed by a **real provider**: **SMTP** (works with SendGrid,
  Mailgun, Postmark, Amazon SES, or any SMTP host) selected by configuration.
- Transactional emails: **registration confirmation**, **password reset**,
  **order received / being processed** (sent with every order), and **shipping/
  status updates**. Templated HTML + plain-text.
- **Local development:** an optional **Mailpit/MailHog** SMTP catcher can be pointed
  at for offline runs, but the code path is the *real* sender — nothing is faked in
  the application layer.
- **Secrets:** the SMTP host/username/password or provider API key live **only** in
  `dotnet user-secrets` (dev) and **GitHub Actions secrets / environment config**
  (CI/deploy). They are **never** committed — enforced by the hardened `.gitignore`
  and `.gitleaks.toml` (see below).

**2FA — real TOTP (already).** Authenticator-app TOTP via Otp.NET with recovery
codes, exactly as arch §6.4. No change needed — it was never mocked.

---

## Q9 — Cloud deployment → **Deferred (hold for v1)** ✅

v1 ships as **`docker compose up`** (api + db + web) only. A cloud deploy (the
author is Azure-experienced) is a clean post-v1 addition — likely App Service /
Container Apps + a managed Postgres, with secrets in Key Vault and **OIDC
federation** (no stored cloud credentials).

---

## Security posture (reaffirmed & hardened)

**Directive:** *nothing sensitive makes it into the repo — the only exception is the
seeded demo accounts.* This holds even as we add real email, Google OAuth, Stripe,
and (later) cloud/tax-service credentials.

- `.gitignore` and `.gitleaks.toml` are **locked down** (this commit): env files,
  keys/certs, cloud/provider credentials, SMTP/email-provider keys, Terraform
  vars/state, kube/npm/pypi credential files, Azure exports, `.claude/` & agent
  artifacts, and logs are all ignored and secret-scanned.
- **Every** real credential (SMTP/SendGrid, Google client secret, Stripe test keys,
  DB password, JWT signing key, and any future Avalara/TaxJar key) lives in
  user-secrets / Actions secrets / OIDC — **never** the repo.
- The **only** allowlisted "credentials" are the documented, throwaway **demo
  accounts** (`admin@widgetworks.demo`, `manager@widgetworks.demo`,
  `demo@widgetworks.demo`).
- Enforcement: pre-commit (gitleaks + detect-private-key + forbidden-artifact guard)
  → CI gitleaks gate → recommend enabling GitHub native **secret scanning + push
  protection**.

---

## New ADRs

| ADR | Decision | Status | Rationale |
|---|---|---|---|
| ADR‑018 | **Manager role** (editable) alongside Customer + immutable Administrator; policy-based authZ | ✅ Accepted | Demonstrates real RBAC; super-admin stays immutable |
| ADR‑019 | **State-level US sales tax** via `ITaxCalculator` (simplified), real tax-service seam | ✅ Accepted | Realistic totals without modeling thousands of jurisdictions |
| ADR‑020 | **Guest checkout + registered tracking**; post-purchase account attach by order email | ✅ Accepted | Low-friction buying + full account features |
| ADR‑021 | **Real transactional email** via `IEmailSender`/SMTP; 2FA real TOTP; provider secrets never committed | ✅ Accepted | Behaves like a real site; keeps secrets out of git |

**Still deferred:** coupons/promotions (post-v1), cloud deploy (post-v1).

---

## Also confirmed — Shopping cart (already in scope)

The **shopping cart** is a core v1 feature and was already specified — flagged here
for completeness:
- PRD §4.1 (Storefront) and user story **E7‑S1 — Cart management**: add / update /
  remove line items, subtotal, quantity capped at **available** stock.
- Data model: **`CARTS`** and **`CART_ITEMS`** (arch §5).
- **Persistence:** a registered user's cart persists across sessions; a guest's cart
  persists for the browser session and **merges into their account on login**.
- The cart feeds checkout → shipping → tax → payment (guest or registered).
