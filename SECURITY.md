# Security Policy

WidgetWorks is a portfolio showcase, but it is built to a production security
posture. This document states what we protect and how.

## Reporting a vulnerability

Please open a private security advisory via the repository's **Security → Report
a vulnerability** tab, or email the maintainer. Do not open a public issue for
security reports.

## Secrets policy — nothing sensitive in git

No secrets, tokens, keys, connection strings, cloud exports, logs, or AI/agent
artifacts are committed to this repository. This is enforced at three layers:

1. **Pre-commit** (`.pre-commit-config.yaml`) — gitleaks + `detect-private-key`
   + a forbidden-artifact guard run before a commit is created.
2. **CI** (`.github/workflows/ci.yml`) — a gitleaks gate fails the build on any
   leaked secret; dependency review blocks high-severity vulnerable packages.
3. **`.gitignore` + `.gitleaks.toml`** — env files, keys/certs, `.claude/` and
   other agent artifacts, logs, and Azure/IaC **exports** are ignored and
   scanned for.

### The one sanctioned exception: demo seed accounts

The seeded **demo admin** (`admin@widgetworks.demo`), **demo manager**
(`manager@widgetworks.demo`) and **demo customer** (`demo@widgetworks.demo`) use
documented, throwaway credentials so reviewers can
log in. These are intentionally public, are the only "credentials" in the repo,
and are allowlisted in `.gitleaks.toml`. They grant access only to a local,
disposable demo database.

## Where real secrets live

- **Local dev:** `dotnet user-secrets` and a git-ignored `.env` (copy from
  `.env.example`).
- **CI/CD:** GitHub Actions **secrets** and **Environments** with required
  reviewers; cloud access via **OIDC/workload-identity federation** — no
  long-lived cloud credentials are stored anywhere.
- **Azure infrastructure:** pulled dynamically by `scripts/get-azure-infra.sh`
  at runtime; its exports are written to the git-ignored `infra/exports/`.

## Automated security tooling

- **CodeQL** (`.github/workflows/codeql.yml`) — `security-extended` queries for
  C# and TypeScript on push, PR, and weekly schedule.
- **Dependabot** (`.github/dependabot.yml`) — version + security updates for
  NuGet, npm, GitHub Actions, and Docker.
- **Secret scanning** — gitleaks in pre-commit and CI. Enable GitHub's native
  **secret scanning + push protection** in repo settings as an additional net.

## Application security highlights

Short-lived JWT access tokens with rotating refresh tokens and reuse detection;
a per-user security stamp for instant "secure my account" invalidation; TOTP
2FA with recovery codes; Google OIDC sign-in that issues our own tokens; account
lockout and auth rate limiting; parameterized SQL (Dapper) throughout; an
immutable seeded admin. See `docs/` for the full design.
