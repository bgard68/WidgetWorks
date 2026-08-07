# WidgetWorks — Product Requirements Document (PRD)

**Working title:** WidgetWorks (an end‑to‑end online widget store)
**Document type:** Pre‑planning / Product Requirements
**Prepared by:** Product Owner (pre‑planning session)
**Date:** August 7, 2026
**Status:** Draft v0.1 — for review
**Companion document:** `02-Architecture-and-Technical-Design.md`

---

## 1. Product vision & purpose

WidgetWorks is a **portfolio showcase**: a fully working, end‑to‑end e‑commerce store that sells "widgets." Its job is not to compete with Amazon on breadth, but to **prove depth** — to demonstrate that the author can design and build a production‑shaped system covering the hard parts most demos skip: real authentication, two‑factor auth, token rotation on compromise, an immutable demo administrator, catalog and inventory management, a checkout with pluggable (mocked) payments, and shipping calculation — all built on clean, testable, time‑abstracted code.

**The one‑sentence pitch:** *A small store that behaves like a real one, so a reviewer can log in, buy a widget, and see production‑grade security and architecture working underneath.*

### What "success" looks like

A reviewer can clone the repo, run one command, and within minutes:

1. Browse a catalog of widgets, add to cart, and check out with a **test** card that visibly succeeds or declines.
2. Register a real account, enable **2FA**, log out, and log back in through the 2FA challenge.
3. Log in as the **fixed demo admin** and add / edit a widget and adjust stock — and confirm that this admin account **cannot be altered or deleted**, so the demo always works.
4. Trigger a "my account was compromised" action and watch every previously issued token **stop working immediately** (token rotation).
5. Read the code and find it clean, layered, and covered by tests that run **deterministically** because time is injected, not read from the wall clock.

---

## 2. Guiding principles & constraints

These are the non‑negotiables that shape every decision downstream.

- **Portfolio‑first, but production‑shaped.** We optimize for "a senior engineer reviewing this nods," not for scale. Where a real store would call Stripe, we call a mock — but behind the *same interface* a real store would use, so the seam is production‑correct.
- **The demo must never break.** The seeded administrator is **immutable**: its credentials, role, and existence are protected in code. No user action (including its own) can change or remove it.
- **Time is a dependency, never ambient.** All time comes from `TimeProvider` (see companion doc). No `DateTime.Now` / `DateTime.UtcNow` anywhere in application code. This makes token expiry, rotation windows, and order timestamps unit‑testable.
- **Security is a first‑class feature, not a bolt‑on.** 2FA, token rotation, lockout, and audit logging are epics in their own right, not afterthoughts.
- **No real card data ever touches our system.** Payment is tokenized/mocked; we never store PANs.
- **Data access via Dapper, no EF Core, no MediatR.** (Rationale and pattern in the companion architecture doc.) We model close to production without heavyweight abstractions.

---

## 3. Target users & personas

| Persona | Role | What they need to do | Notes |
|---|---|---|---|
| **Shopper (Guest)** | Unauthenticated visitor | Browse catalog, search/filter, add to cart, see shipping cost | Can build a cart; must register/log in to check out |
| **Shopper (Registered)** | Authenticated customer | Everything a guest can, plus checkout, order history, manage profile, enable 2FA, "secure my account" | The primary happy path |
| **Store Administrator** | Fixed demo admin | Manage widgets (CRUD), manage inventory/stock, view all orders, update order status | **Immutable seeded account** — cannot be changed/deleted |
| **Reviewer / Evaluator** | The person judging the portfolio | Run it fast, read the code, poke the security features | Not a system role, but the real audience — drives "easy to run" and "obviously correct" |

> **Open question (Q1):** Do we want a second, *editable* admin role (e.g., "Manager") to demonstrate role management, while keeping the seeded super‑admin immutable? See §11.

---

## 4. Scope

### 4.1 In scope (the product)

**Storefront**

- Widget catalog with categories, **product image**, **description**, price, and stock status (**quantity on hand** and **available** = on‑hand − reserved).
- Search and filter (by name, category, price range, in‑stock).
- Product detail page.
- Shopping cart (guest + registered), persisted for registered users.
- Checkout: shipping address, shipping method selection, cost calculation, order review, mock payment, confirmation.
- Order history and order detail for registered users.

**Identity & security**

- Registration, email confirmation (mocked email), login, logout.
- **Sign in with Google** (OIDC) as an additional login option — linked to the same account and issuing our own tokens.
- Password hashing, password reset (mocked email link).
- **Two‑factor authentication (TOTP / authenticator app)** with recovery codes.
- **JWT access + refresh tokens** with **refresh‑token rotation**.
- **"Secure my account" / compromise response**: one action invalidates all outstanding tokens everywhere.
- Account lockout / brute‑force protection.
- Role‑based authorization (Customer vs Administrator).
- **Immutable seeded administrator.**

**Administration**

- Widget management: create, edit, deactivate (soft delete), set price and images.
- Inventory management: set / adjust stock levels.
- Order management: view orders, advance order status (e.g., Paid → Fulfilled → Shipped).

**Cross‑cutting**

- Shipping cost calculation (pluggable strategy).
- Mock payment gateway behind a production‑shaped interface.
- Audit / security event logging.
- Seed data so the store is populated and demo‑ready on first run.
- API documentation (OpenAPI/Swagger).

### 4.2 Out of scope (for v1 — candidates for "future work")

Explicitly *not* building these keeps the demo focused; each is a talking point for "what production would add."

- Real payment processing / PCI compliance (we mock; we note Stripe as the swap‑in).
- Real transactional email delivery (we log/preview instead).
- Multi‑currency, internationalization/localization.
- Tax engine integration (we either omit tax or apply a simple flat rate — see Q3).
- Product reviews & ratings, wishlists, recommendations.
- Returns/refunds/RMA workflow (may stub the state only).
- Warehousing, multi‑location inventory, dropshipping.
- Marketing: coupons/promotions (candidate stretch goal — see Q4).
- Mobile native apps.
- Horizontal scaling, multi‑tenant, high‑availability infrastructure.

---

## 5. Epics & feature breakdown

The backlog is organized into eight epics. Each rolls up user stories in §6.

| # | Epic | Goal | Priority |
|---|---|---|---|
| E1 | **Foundations & platform** | Solution scaffold, DB, migrations, TimeProvider, CI, seed data, Swagger | Must (first) |
| E2 | **Identity & authentication** | Register, login, JWT+refresh rotation, password reset | Must |
| E3 | **Two‑factor authentication** | TOTP enrollment, challenge, recovery codes | Must |
| E4 | **Account security & compromise response** | Lockout, "secure my account," global token invalidation, audit log | Must |
| E5 | **Immutable administration** | Seeded protected admin, admin authZ, admin console (widgets/inventory/orders) | Must |
| E6 | **Catalog & inventory** | Widgets, categories, stock, search/filter, product pages | Must |
| E7 | **Cart, shipping & checkout** | Cart, shipping calculation, mock payment, order creation | Must |
| E8 | **Orders & post‑purchase** | Order history, order status lifecycle, mock notifications | Should |

---

## 6. User stories & acceptance criteria

Format: *As a [persona], I want [capability], so that [benefit].* Acceptance criteria use **Given/When/Then**. IDs map to epics.

### E1 — Foundations & platform

**E1‑S1 — Runnable in one command**
*As a reviewer, I want to run the whole stack with a single command, so that I can evaluate it in minutes.*
- **Given** a clean machine with the required runtime/Docker, **when** I run the documented start command, **then** the API, database, and front end come up and the store shows seeded widgets.
- **Given** first run, **then** the database is created/migrated and seed data is present: categories, sample **widgets** (each with name, description, product image, price, and opening quantity on hand), an **immutable demo admin** (manages widgets/inventory/orders), and a **demo customer** (shopper flow) — both accounts documented in the README.

**E1‑S2 — Deterministic time**
*As a developer, I want all time sourced from an injected provider, so that time‑dependent logic is testable.*
- **Given** any code path that needs "now," **then** it obtains time from the injected `TimeProvider`, never from `DateTime.Now/UtcNow`.
- **Given** a unit test, **when** it substitutes a fake time provider, **then** token expiry / rotation windows behave deterministically.

**E1‑S3 — Discoverable API**
*As a reviewer, I want interactive API docs, so that I can explore endpoints without the UI.*
- **Given** the API is running, **when** I open the docs endpoint, **then** all public endpoints, auth requirements, and schemas are visible and callable.

### E2 — Identity & authentication

**E2‑S1 — Register**
*As a shopper, I want to create an account, so that I can check out and track orders.*
- **Given** a valid, unused email and a policy‑compliant password, **when** I register, **then** my account is created with the **Customer** role and a confirmation email is generated (previewable).
- **Given** an email already in use, **then** registration fails with a clear, non‑enumerating message.
- **Given** a weak password, **then** it is rejected with the policy stated.
- Passwords are stored **only** as a salted hash.

**E2‑S2 — Log in and receive tokens**
*As a registered shopper, I want to log in, so that I can access my account.*
- **Given** correct credentials **and no 2FA**, **when** I log in, **then** I receive a short‑lived **access token** and a **refresh token**.
- **Given** correct credentials **and 2FA enabled**, **then** I am issued a limited **2FA challenge token** and prompted for a code (see E3), **not** full access.
- **Given** incorrect credentials, **then** login fails without revealing whether the email exists.

**E2‑S3 — Refresh with rotation**
*As a signed‑in shopper, I want my session to refresh seamlessly, so that I stay logged in without re‑entering credentials, safely.*
- **Given** a valid, unexpired refresh token, **when** I refresh, **then** I receive a new access token **and a new refresh token**, and the **old refresh token is immediately invalidated** (rotation).
- **Given** a refresh token that has already been used (replay), **then** the request is rejected **and** the whole token family is revoked (reuse detection).
- **Given** an expired or revoked refresh token, **then** refresh fails and I must log in again.

**E2‑S4 — Log out**
*As a signed‑in shopper, I want to log out, so that my session ends.*
- **Given** I log out, **then** my current refresh token is revoked and can no longer be used.

**E2‑S5 — Password reset (mocked email)**
*As a shopper who forgot my password, I want to reset it, so that I can regain access.*
- **Given** I request a reset, **then** a time‑limited reset link is generated (previewable), regardless of whether the email exists (no enumeration).
- **Given** a valid, unexpired reset token, **when** I set a new password, **then** it is updated **and all existing sessions are invalidated** (see E4 mechanism).

**E2‑S6 — Sign in with Google**
*As a shopper, I want to log in with my Google account, so that I don't need a separate password.*
- **Given** I choose "Sign in with Google," **when** I complete Google's consent, **then** I'm returned authenticated and issued **our** access + refresh tokens (same rotation model).
- **Given** my Google email matches an existing account, **then** the Google identity links to it; **given** it's new, **then** a Customer account is created.
- **Given** I signed in with Google, **then** the immutable admin path is unaffected — the demo admin remains local‑password only.

### E3 — Two‑factor authentication

**E3‑S1 — Enroll in 2FA**
*As a security‑conscious shopper, I want to enable authenticator‑app 2FA, so that my account is harder to breach.*
- **Given** I start enrollment, **then** I receive a TOTP secret + QR code and must confirm one correct code before 2FA is switched on.
- **Given** 2FA is enabled, **then** I am issued **one‑time recovery codes** shown exactly once.

**E3‑S2 — 2FA challenge at login**
*As a shopper with 2FA on, I want to enter a code at login, so that a stolen password alone is not enough.*
- **Given** a valid 2FA challenge token, **when** I submit a valid TOTP code within its time window, **then** I receive full access + refresh tokens.
- **Given** an invalid/expired code, **then** the challenge fails and counts toward lockout.
- **Given** I use a valid **recovery code**, **then** I authenticate and that code is consumed (single use).

**E3‑S3 — Disable 2FA**
*As a shopper, I want to turn off 2FA (re‑authenticating), so that I keep control of my account.*
- **Given** I re‑verify identity, **when** I disable 2FA, **then** the secret and recovery codes are invalidated and a security event is logged.

### E4 — Account security & compromise response

**E4‑S1 — Brute‑force lockout**
*As the system, I want to lock accounts after repeated failures, so that credential stuffing is slowed.*
- **Given** N consecutive failed login/2FA attempts, **then** the account is temporarily locked and further attempts are refused until the lockout window (measured via `TimeProvider`) elapses.

**E4‑S2 — "Secure my account" (compromise response)**
*As a shopper who fears compromise, I want a single action that boots everyone out, so that a thief's session dies instantly.*
- **Given** I trigger "secure my account," **then** a **security stamp** on my account is rotated, which **immediately invalidates every outstanding access and refresh token** across all devices.
- **Given** any previously issued token is presented afterward, **then** it is rejected as stale.
- **Given** the action completes, **then** I am prompted to set a new password and re‑enroll 2FA if desired, and a security event is recorded.

**E4‑S3 — Admin‑initiated / signing‑key rotation**
*As the platform, I want to rotate JWT signing keys, so that a leaked key can be retired without downtime.*
- **Given** signing keys are rotated, **then** newly issued tokens use the new key (identified by `kid`) while tokens signed by the still‑trusted previous key validate until expiry; retired keys are rejected. *(See companion doc for two‑level rotation: per‑user security stamp vs. global signing key.)*

**E4‑S4 — Security audit log**
*As an admin/reviewer, I want security‑relevant events recorded, so that I can trace what happened.*
- **Given** events such as login success/failure, 2FA enable/disable, token refresh reuse detection, and "secure my account," **then** each is written to an audit log with actor, action, timestamp (from `TimeProvider`), and source metadata.

### E5 — Immutable administration

**E5‑S1 — Seeded immutable admin**
*As the product owner, I want a fixed admin account that can never change, so that the demo always works.*
- **Given** the system seeds on first run, **then** exactly one protected **Administrator** exists with known demo credentials (documented in the README).
- **Given** any request to modify, disable, demote, or delete the protected admin — **including a request made by that admin** — **then** it is rejected at the domain layer with a clear error, and the attempt is audited.
- **Given** the protected admin, **then** it is exempt from lockout deletion and password‑expiry removal so it can always log in for the demo.

> **Design note:** "Never change" is enforced by a guard in the domain/service layer keyed off the protected admin's identity, **and** defensively at the data layer — not merely by UI hiding the buttons.

**E5‑S2 — Admin: manage widgets**
*As an admin, I want to add and edit widgets, so that the catalog stays current.*
- **Given** I am the admin, **when** I create a widget with valid fields (name, description, price, category, image, initial stock), **then** it appears in the catalog.
- **Given** I edit price/description/image or **deactivate** a widget, **then** the storefront reflects it; deactivation is a **soft delete** (order history remains intact).
- **Given** a non‑admin, **then** all admin endpoints return authorization failures.

**E5‑S3 — Admin: manage inventory**
*As an admin, I want to adjust stock, so that availability is accurate.*
- **Given** I set/adjust stock for a widget, **then** availability and "in stock" flags update, and checkout respects the new level.

**E5‑S4 — Admin: manage orders**
*As an admin, I want to view and advance orders, so that fulfillment is tracked.*
- **Given** an order exists, **when** I advance its status along the allowed lifecycle, **then** the customer's order view reflects the new status; illegal transitions are rejected.

### E6 — Catalog & inventory

**E6‑S1 — Browse catalog** — *Given* the store, *when* I visit it, *then* I see active widgets with product image, name, description snippet, price, and availability (in‑stock driven by **available** quantity), paginated.
**E6‑S2 — Search & filter** — *Given* the catalog, *when* I search by term and/or filter by category, price range, and in‑stock, *then* results update accordingly.
**E6‑S3 — Product detail** — *Given* a widget, *when* I open it, *then* I see full details and can add a valid quantity to my cart (bounded by stock).

### E7 — Cart, shipping & checkout

**E7‑S1 — Cart management**
- *Given* a widget in stock, *when* I add/update/remove it, *then* my cart reflects line items, quantities, and subtotal.
- *Given* a registered user, *then* my cart persists across sessions; *given* a guest, *then* the cart persists for the browser session and merges on login.
- *Given* I add more than available stock, *then* the quantity is capped and I'm told why.

**E7‑S2 — Shipping calculation**
*As a shopper, I want to see shipping cost before paying, so that there are no surprises.*
- *Given* a cart and a destination + chosen shipping method, *when* I reach checkout, *then* a shipping cost is calculated and shown, along with an estimated delivery window.
- *Given* the order subtotal exceeds a configured threshold, *then* free shipping applies (if that strategy is enabled).
- Shipping is computed by a **pluggable strategy** (flat / weight‑based / zone‑based — see companion doc).

**E7‑S3 — Checkout & mock payment**
*As a shopper, I want to pay and place an order, so that I receive my widgets.*
- *Given* a valid cart, address, and shipping method, *when* I submit a **test** card, *then* the mock gateway returns approve/decline **deterministically** based on the test card number, and I see the result.
- *Given* an **approved** payment, *then* an order is created (status *Paid*), stock is decremented atomically, the cart is cleared, and a confirmation (with order number) is shown; a confirmation email is generated (previewable).
- *Given* a **declined** payment, *then* no order is created, no stock is decremented, and I can retry.
- *Given* two concurrent checkouts for the last unit, *then* only one succeeds; the other is told the item is out of stock (no overselling).
- *Given* a duplicate submit (double‑click / retry), *then* an **idempotency** guard prevents a second charge/order.
- **No real card number is ever stored**; only a masked last‑4 and a mock transaction reference are retained.

### E8 — Orders & post‑purchase

**E8‑S1 — Order history** — *Given* a registered user, *when* I open my orders, *then* I see past orders with status, totals, and line items; *when* I open one, *then* I see full detail.
**E8‑S2 — Order lifecycle** — *Given* an order, *then* its status follows an allowed state machine (e.g., *Paid → Fulfilled → Shipped → Delivered*, with *Cancelled* where permitted); illegal transitions are rejected.
**E8‑S3 — Notifications (mocked)** — *Given* order placement and status changes, *then* notification emails are generated and previewable (no real send in v1).

---

## 7. Non‑functional requirements (NFRs)

**Security**
- Passwords hashed with a modern, salted algorithm; never logged or returned.
- Access tokens short‑lived (target ~10–15 min); refresh tokens long‑lived, rotated on every use, stored **hashed**.
- All state‑changing endpoints require authentication and appropriate role; admin endpoints reject non‑admins.
- Transport is HTTPS end‑to‑end; secrets are configuration, never committed.
- No sensitive data (passwords, full card numbers, TOTP secrets, recovery codes) in logs.
- Input validation on every write; guard against injection (parameterized queries throughout — Dapper).
- Rate limiting on auth endpoints.

**Reliability & correctness**
- Stock decrement and order creation are **atomic** (transaction) with no overselling under concurrency.
- Checkout is **idempotent**.
- Deterministic, repeatable tests via injected time.

**Performance (demo‑appropriate)**
- Catalog and cart operations feel instant on a single‑node local run (target < 300 ms typical).
- Pagination on catalog and order lists.

**Maintainability & quality**
- Layered/vertical‑slice architecture (companion doc), Dapper for data access, no MediatR, no EF Core.
- Meaningful unit tests for domain/security logic and integration tests for the key flows (auth, checkout).
- Structured logging.
- CI runs build + tests on every push.

**Usability & operability**
- One‑command run; seeded, demo‑ready data.
- README documents demo admin credentials, test card numbers, and how to exercise 2FA and "secure my account."
- Interactive API docs (Swagger/OpenAPI).

**Accessibility (storefront)**
- Reasonable semantic HTML and keyboard navigability on core flows (nice‑to‑have for a portfolio, flagged not gated).

---

## 8. Assumptions

- Single‑node, local‑first deployment (Docker Compose) is sufficient for the showcase; cloud deploy is a stretch goal.
- Email is **mocked** (rendered to a preview inbox or log), not actually delivered.
- Payment is **mocked** behind a real‑shaped interface; no PCI scope.
- A single storefront locale/currency (e.g., USD) is fine for v1.
- The reviewer has the documented runtime/Docker available.

---

## 9. Dependencies

- .NET 10 (current LTS) runtime/SDK. (`TimeProvider` available since .NET 8.)
- A relational database engine (selection in companion doc) runnable via Docker.
- Dapper for data access; a lightweight migration runner (no EF migrations).
- A TOTP library for 2FA; a JWT library for tokens.
- Front‑end toolchain (selection in companion doc).
- Container runtime (Docker) for one‑command run.

---

## 10. Phased roadmap (release plan)

Each phase is independently demoable; ship vertically, not layer‑by‑layer.

**Phase 0 — Foundations (E1).** Solution scaffold, DB + migration runner, `TimeProvider` wiring, seed framework, Swagger, CI, one‑command run. *Exit:* empty store runs, docs open, tests green.

**Phase 1 — Identity core (E2).** Register, login, JWT + refresh **rotation**, logout, password reset (mock email). *Exit:* a user can register and stay logged in with rotating tokens; reuse detection works.

**Phase 2 — Security hardening (E3 + E4).** TOTP 2FA + recovery codes, lockout, **"secure my account"** global invalidation, signing‑key rotation, audit log. *Exit:* the headline security demo works end‑to‑end.

**Phase 3 — Immutable admin + catalog (E5 + E6).** Seeded immutable admin, admin widget/inventory management, storefront browse/search/detail. *Exit:* admin can stock the store; shoppers can browse; admin immutability proven.

**Phase 4 — Commerce (E7).** Cart, shipping calculation, mock payment, atomic order creation with idempotency and no overselling. *Exit:* a shopper can buy a widget end‑to‑end.

**Phase 5 — Orders & polish (E8 + NFRs).** Order history/lifecycle, mock notifications, rate limiting, observability, README/demo script, optional cloud deploy. *Exit:* portfolio‑ready.

> Phases 1–4 are the "must" spine. Phase 5 is "should." Stretch goals (§11) are "could."

---

## 11. Open questions & decisions to make

These are the gaps I'd want your call on before we finalize the backlog.

- **Q1 — Roles:** Just Customer + one immutable super‑admin? Or add an editable "Manager" role to demo role management (super‑admin still immutable)?
- **Q2 — Front end:** ✅ **RESOLVED — React + TypeScript SPA** (tech stack approved Aug 7, 2026).
- **Q3 — Tax:** Omit tax entirely, apply a simple configurable flat rate, or a tiny zone‑based table? (Affects order‑total math.)
- **Q4 — Promotions:** Include a coupon/discount code feature as a stretch goal, or leave it out?
- **Q5 — Guest checkout:** Allow checkout **without** an account, or require registration to buy? (Assumed registration required to check out, guest browsing allowed.)
- **Q6 — Payment mock fidelity:** ✅ **RESOLVED — both:** pure mock as the zero‑setup default **and** a Stripe test‑mode adapter behind the same `IPaymentGateway` (Aug 7, 2026).
- **Q7 — Database engine:** ✅ **RESOLVED — PostgreSQL 16** (tech stack approved Aug 7, 2026).
- **Q8 — Notifications:** Preview inbox (e.g., a local mail catcher) vs. just logging rendered emails?
- **Q9 — Cloud deploy:** Is a live hosted demo URL in scope, or is "clone and run locally" enough?

---

## 12. Risks

| Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|
| Security features (rotation, 2FA, key rotation) are subtle and easy to get *almost* right | High | Medium | Treat as first‑class epics; focused tests for reuse detection, stamp invalidation, and clock‑edge cases using fake time |
| Scope creep toward "a real Amazon" | Medium | High | Hard "out of scope" list (§4.2); phases are vertical and shippable |
| Overselling / double‑charge under concurrency | High | Medium | Atomic stock decrement in a transaction; idempotency keys on checkout; concurrency tests |
| Ambient `DateTime` sneaks in, breaking testability | Medium | Medium | Lint/review rule banning `DateTime.Now/UtcNow`; inject `TimeProvider` everywhere |
| Mock payment feels fake to reviewers | Low/Medium | Medium | Deterministic test‑card behavior + same interface a real gateway uses; optional Stripe test mode (Q6) |
| "Immutable admin" only enforced in UI | High | Low | Enforce in domain + data layer, not UI; add a test that *attempts* to mutate it and asserts rejection |

---

*End of PRD. See `02-Architecture-and-Technical-Design.md` for the technical proposal that satisfies these requirements.*
