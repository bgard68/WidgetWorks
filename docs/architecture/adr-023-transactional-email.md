# ADR-023 — Transactional email

**Status:** Accepted
**Date:** August 7, 2026
**Context:** Phase 5 sends real transactional email (order received, shipped, cancelled; later
password reset and registration confirmation). We need real delivery without committing any secret and
without requiring a mail server for local runs or CI.

## Decision

`IEmailSender` is the seam. Two implementations, selected by `Email:Provider`:

- **`SmtpEmailSender`** — real delivery via `System.Net.Mail` against any SMTP host (SendGrid,
  Mailgun, Postmark, Amazon SES, or a local Mailpit/MailHog catcher). Host, port, and credentials come
  only from configuration / `dotnet user-secrets` / deploy env — never the repo. Sends multipart
  (HTML + plain text).
- **`DevEmailSender`** (default) — writes the message to stdout so it's visible in the app log with no
  mail server. Used for local runs and CI.

Sending is **best-effort** at the call sites: a notification failure never rolls back a paid order or a
status transition.

## Production upgrade

`System.Net.Mail` is sufficient here and dependency-free. For production, swap `SmtpEmailSender` for a
**MailKit**-based implementation (better STARTTLS/OAuth support and modern SMTP handling) — same
`IEmailSender` seam, no change to callers — or point `Email:Provider=Smtp` at a provider's SMTP
relay / API. Templates live in `EmailTemplates`; a templating engine or provider-hosted templates can
replace them without touching handlers.
