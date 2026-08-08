# Local development & configuration

How to supply configuration (including secrets) when running WidgetWorks. The one rule that never
bends: **no secret is ever committed to git.** ASP.NET Core layers configuration sources; later
sources win:

```
appsettings.json  <  user-secrets (Development only)  <  environment variables
```

Pick the mechanism that matches how you're running the app.

## 1) Running the API directly (`dotnet run`) — use user-secrets

`dotnet user-secrets` stores values in your OS user profile
(`~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`), **outside the repo**. They load
automatically in the `Development` environment. This is the idiomatic choice for local dev and is
**dev-only** — Microsoft explicitly does not intend it for production.

The API project already declares a `UserSecretsId`, so this works out of the box:

```bash
cd src/WidgetWorks.WebApi

# JWT signing key (generate a strong one)
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"

# Database (or set Postgres:* individually)
dotnet user-secrets set "ConnectionStrings:WidgetWorks" \
  "Host=localhost;Port=5432;Database=widgetworks;Username=widgetworks;Password=<your-local-pw>"

# Demo seed passwords (throwaway)
dotnet user-secrets set "Seed:DemoAdminPassword" "DemoAdmin!Change01"
dotnet user-secrets set "Seed:DemoCustomerPassword" "DemoUser!Change01"

# Optional integrations
dotnet user-secrets set "Google:ClientId" "<public-client-id>.apps.googleusercontent.com"
dotnet user-secrets set "Payments:Provider" "Stripe"
dotnet user-secrets set "Payments:Stripe:SecretKey" "sk_test_..."   # TEST keys only

dotnet run
```

Use `dotnet user-secrets list` to see what's set. Nothing here touches the repo.

## 2) Running via Docker (`docker compose`) — use a git-ignored `.env`

Containers can't read your host's user-secrets, so compose supplies configuration through environment
variables. The standard local mechanism is a **git-ignored `.env`** (12-factor style):

```bash
cp .env.example .env       # then edit; set POSTGRES_PASSWORD and Jwt__SigningKey at minimum
docker compose up --build
```

`.env` is ignored by `.gitignore` (only `*.env.example` is allowed) and gitleaks scans every push, so
a real `.env` can't be committed. Keys use the double-underscore convention that maps to config
sections (`Jwt__SigningKey` -> `Jwt:SigningKey`).

## 3) CI and production — platform secret stores

Neither user-secrets nor a committed file. Use:

- **GitHub Actions** — repository/environment **Secrets** (and **Variables** for public, non-secret
  values like `VITE_*` used at web build time).
- **Cloud** — **Azure Key Vault** (with OIDC federation, no stored cloud credentials), AWS Secrets
  Manager, or the orchestrator's secret mechanism, injected as environment variables at runtime.

Because environment variables sit at the top of the configuration precedence order, the same code
reads them with no changes across all three contexts.

## Summary

| Context | Mechanism | Committed? |
|---|---|---|
| `dotnet run` (local) | `dotnet user-secrets` | No (OS user profile) |
| `docker compose` (local) | git-ignored `.env` | No |
| CI | GitHub Actions Secrets / Variables | No |
| Production | Key Vault / secret manager -> env vars | No |

The only configuration file tracked in git is **`.env.example`** — a template with placeholder values,
allow-listed in `.gitleaks.toml`.
