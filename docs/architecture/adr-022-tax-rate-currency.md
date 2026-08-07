# ADR-022 — Keeping sales-tax rates current

**Status:** Accepted
**Date:** August 7, 2026
**Context:** Phase 4b introduced `ITaxCalculator` with a built-in state-level rate table. Rates
change over time, so a hard-coded table goes stale. How should the system keep rates current?

## How real stores handle this

Production e-commerce does **not** hand-maintain rate tables. The two established patterns are:

1. **Tax-as-a-service (the common choice).** Call a provider at checkout — **Avalara AvaTax**,
   **TaxJar**, **Stripe Tax**, or **Vertex**. The provider returns a rooftop-accurate rate for the
   destination and owns keeping rates, jurisdiction boundaries, product-category exemptions, and
   economic-nexus rules current. You never update a table yourself.
2. **Scheduled import of an authoritative dataset.** For teams that can't use a paid service, a
   background job periodically pulls an official rates dataset (e.g., a state revenue department
   publication or a licensed rates file) into the application database on a cadence, with an
   effective date per row.

## Decision

Make the **rate source** a seam of its own — `ITaxRateProvider` — separate from the calculator:

- `StaticStateTaxRateProvider` is the offline default. It exposes a `TaxRateSet` carrying the rates
  plus an **`EffectiveOn`** date and a **`Source`** label, so staleness is visible (surfaced at
  `GET /checkout/tax-info`).
- Swapping in currency is now a provider change, not a checkout change:
  - **Live provider:** implement `ITaxCalculator` directly against Avalara/TaxJar/Stripe Tax (their
    API already returns the amount), or implement `ITaxRateProvider` to read a cached rate set the
    provider syncs.
  - **Scheduled refresh:** implement `ITaxRateProvider` over a `tax_rates` table and add a
    background `IHostedService` (or an out-of-process cron) that refreshes it from the authoritative
    dataset. The calculator and checkout are untouched.

## Consequences

- The portfolio ships with a clearly-labeled, versioned offline table — correct for a demo and
  honest about its limits.
- Production readiness is a drop-in: pick a provider adapter or add a refresh job; no changes to
  `StateSalesTaxCalculator`, `QuoteCartHandler`, or any endpoint.
- Because tax is computed **server-side at checkout**, upgrading the source immediately affects new
  orders without client changes.
