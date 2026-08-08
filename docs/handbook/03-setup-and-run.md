[← Handbook index](README.md) · [Project README](../../README.md)

# 3. Setup & run

## Option A — one command (Docker)

**Prerequisite:** Docker Desktop installed and **running**. You do not need .NET or Node
installed — Docker builds both.

```powershell
git clone https://github.com/bgard68/WidgetWorks.git
cd WidgetWorks
copy .env.example .env      # placeholder values are valid for a local run
docker compose up --build
```

The first build takes a few minutes (it pulls the .NET SDK image, restores NuGet, and
runs the Vite build). Wait until all three containers are up:

```powershell
docker compose ps          # db (healthy), api (running), web (running)
```

Then open:

| What | URL |
|---|---|
| Store (SPA) | http://localhost:3000 |
| API + Scalar (interactive API UI) | http://localhost:8080/scalar/v1 |
| Health | http://localhost:8080/health |

Migrations and demo seed run automatically on API start.

### Demo accounts

| Role | Email | Password (from `.env`) |
|---|---|---|
| Administrator (immutable) | `admin@widgetworks.demo` | `DemoAdmin!Change01` |
| Customer | `demo@widgetworks.demo` | `DemoUser!Change01` |

The seeded admin has **no 2FA** by default, so it logs straight in with email + password.

### Stop / restart

```powershell
docker compose down        # stop (keeps the pgdata volume)
docker compose up          # start again (no --build unless code changed)
```

## Option B — hybrid dev (run the API on the host)

Useful when you want fast iteration and to keep the API’s secrets in **user-secrets**
instead of a file. Run only Postgres in a container; run the API and web on the host.

```powershell
docker compose up db        # just Postgres

cd src/WidgetWorks.WebApi
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)"
dotnet user-secrets set "ConnectionStrings:WidgetWorks" "Host=localhost;Port=5432;Database=widgetworks;Username=widgetworks;Password=<your-local-pw>"
dotnet user-secrets set "Seed:DemoAdminPassword" "DemoAdmin!Change01"
dotnet user-secrets set "Seed:DemoCustomerPassword" "DemoUser!Change01"
dotnet run                  # API on http://localhost:5080  (Scalar UI at /scalar/v1)

cd ../../web
copy .env.example .env.local # VITE_API_BASE_URL already points at http://localhost:5080
npm install
npm run dev                 # SPA on http://localhost:5173
```

> **Why 5080 / why user-secrets load:** `dotnet run` uses the profile in
> `src/WidgetWorks.WebApi/Properties/launchSettings.json`, which sets
> `ASPNETCORE_ENVIRONMENT=Development` and binds `http://localhost:5080`. The
> Development environment is what makes .NET read the `dotnet user-secrets` values you
> just set (in Production they are ignored). Port `5080` matches the SPA’s default
> `VITE_API_BASE_URL`, so the web app talks to the API with no extra config. The CORS
> policy already allows the Vite dev origin (`http://localhost:5173`).

See [Configuration](04-configuration-and-2fa.md) for which mechanism supplies which
values and why.

## Troubleshooting

- **“localhost refused to connect”** — the stack isn’t up yet; wait for the build to
  finish and `docker compose ps` to show all services running.
- **“port is already allocated”** — something else is on 3000 / 8080 / 5432; stop it or
  remap the port in `docker-compose.yml`.
- **API exits immediately / “signing key” or DB password errors under `dotnet run`** —
  you’re almost certainly not in the Development environment (so user-secrets didn’t
  load). Confirm `launchSettings.json` is present, or run
  `dotnet run --environment Development`.
- **Product images blank** — the sample photos come from an external service (picsum);
  it just needs internet. Everything else works offline.
