<!-- markdownlint-disable MD013 -->

# 10. Deploying to Azure on free tiers

Runs the whole stack for **$0/month**: the API on a Free (F1) App Service, the SPA on a Free
Static Web App, secrets in Key Vault, and PostgreSQL on Neon's permanent free plan.

Azure has **no free PostgreSQL tier** — the free-account grant is time-boxed and does not apply
to a Pay-As-You-Go subscription. Azure SQL's free offer (10 databases per subscription, for the
lifetime of the subscription) is real but is SQL Server, not Postgres, so it would mean porting
every migration and repository. Hosting Postgres on Neon keeps the stack and the bill.

Every value below is **queried, never pasted** — no subscription id, hostname or principal id is
hard-coded, so the script survives being re-run against a different subscription.

---

## Ground rules

**Never deploy the repository.** Publish the API to a clean folder and deploy *only that folder*.
A `az webapp up` or a zip taken from the repo root pushes source, `.env`, `node_modules` and git
history into `wwwroot`, where they are served over HTTP. That is source disclosure and a
credential leak in one step. The SPA does not belong in `wwwroot` either — it goes to Static Web
Apps, which is a separate service with its own CDN.

**No secret ever reaches a command line, an app setting or a file in the repo.** Secrets go into
Key Vault from a temporary file that is deleted, and App Service holds only
`@Microsoft.KeyVault(...)` references resolved at runtime by a managed identity.

### The startup crash loop — read this before deploying

`Program.cs` runs `MigrationRunner.Run(connectionString)` at line 88, **before** `app.Run()`. DbUp
throws if it cannot reach or migrate the database, the process exits, App Service restarts it, and
it fails again — a loop that quietly consumes the F1 tier's **60 CPU-minutes per day**. A wrong
connection string does not produce a broken page; it produces a day of burnt quota.

Two habits make this a non-event:

**Prove the connection string works before it goes anywhere near Azure.**

```bash
psql "postgresql://<user>:<pw>@<host>.neon.tech/widgetworks?sslmode=require" -c "select 1"
```

**Watch the first boot, and stop the app the moment it loops.**

```bash
az webapp log tail --name $APP --resource-group $RG   # ctrl-c when healthy
az webapp stop --name $APP --resource-group $RG       # stops the bleeding instantly
```

A stopped app burns nothing. Fix the setting, then `az webapp start`.

Neon suspends after 5 minutes idle, so the very first connection may arrive while the database is
still waking. Give the connection string room to wait rather than letting DbUp fail the boot:

```text
;Timeout=30;Command Timeout=60
```

---

## 0. Shared variables

```bash
LOC=centralus                     # free Azure SQL is region-locked per subscription; match it
RG=rg-widgetworks
SUFFIX=$(az account show --query id -o tsv | cut -c1-6)   # stable per-subscription suffix
KV=widgetworks-kv-$SUFFIX         # vault names are globally unique
APP=widgetworks-api-$SUFFIX       # app names are globally unique
PLAN=plan-widgetworks-free
SWA=widgetworks-web
```

Confirm the names are free before creating anything:

```bash
az keyvault list --query "[?name=='$KV'].name" -o tsv
az webapp list --query "[?name=='$APP'].name" -o tsv
```

---

## 1. Resource group

Its own group, so the whole project can be deleted in one command and RBAC can be scoped to it.

```bash
az group create --name $RG --location $LOC
```

---

## 2. PostgreSQL on Neon

Manual, one time — Neon has no Azure CLI.

1. Sign up at <https://neon.com> (no credit card; the free plan is permanent, not a trial).
2. Create a project, region closest to `centralus`.
3. Copy the **pooled** connection string.

Rewrite it into the form the app expects, and keep `SSL Mode=Require`:

```text
Host=<host>.neon.tech;Database=widgetworks;Username=<user>;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true;Timeout=30;Command Timeout=60
```

The timeouts are not optional padding — see the crash-loop note above. `BuildConnectionString`
reads `ConnectionStrings:WidgetWorks` first and only falls back to the `Postgres:*` keys, so
setting `ConnectionStrings__WidgetWorks` alone is correct and the `Postgres__*` keys are unused
in Azure.

Neon suspends compute after 5 minutes idle and wakes on the next connection, so the first request
after a quiet spell is slow — the same cold start F1 already has.

---

## 3. Key Vault

RBAC rather than access policies: role assignments are auditable and scope to the resource.

```bash
az keyvault create --name $KV --resource-group $RG --location $LOC \
  --enable-rbac-authorization true --enable-purge-protection true
```

Grant yourself write access (Contributor cannot read secret values):

```bash
ME=$(az ad signed-in-user show --query id -o tsv)
VAULT_ID=$(az keyvault show --name $KV --query id -o tsv)
az role assignment create --role "Key Vault Secrets Officer" --assignee-object-id $ME \
  --assignee-principal-type User --scope $VAULT_ID
```

Add the secrets **from files**, so nothing lands in shell history, then shred the files:

```bash
printf '%s' 'Host=...;SSL Mode=Require' > .secret && \
  az keyvault secret set --vault-name $KV --name "ConnectionStrings--WidgetWorks" --file .secret

openssl rand -base64 48 > .secret && \
  az keyvault secret set --vault-name $KV --name "Jwt--SigningKey" --file .secret

rm -f .secret
```

Key Vault names cannot contain `:` or `_`, so `Jwt__SigningKey` is stored as `Jwt--SigningKey`.
Add `Email--Password`, `Payments--Stripe--SecretKey` and `Payments--Stripe--WebhookSecret` the same
way only if you move off the Dev/Mock providers.

---

## 4. App Service (Free F1)

```bash
az appservice plan create --name $PLAN --resource-group $RG --location $LOC --sku F1 --is-linux

az webapp create --name $APP --resource-group $RG --plan $PLAN --runtime "DOTNETCORE:10.0"
```

Harden it before it holds anything:

```bash
az webapp update --name $APP --resource-group $RG --https-only true

az webapp config set --name $APP --resource-group $RG \
  --min-tls-version 1.2 --ftps-state Disabled --http20-enabled true
```

`--https-only` redirects HTTP; `--ftps-state Disabled` closes the plaintext publish channel, which
is the most commonly forgotten hole on App Service.

---

## 5. Managed identity → Key Vault

The app authenticates to Key Vault as itself. No secret is involved, so none can leak.

```bash
PRINCIPAL=$(az webapp identity assign --name $APP --resource-group $RG --query principalId -o tsv)

az role assignment create --role "Key Vault Secrets User" --assignee-object-id $PRINCIPAL \
  --assignee-principal-type ServicePrincipal --scope $VAULT_ID
```

`Key Vault Secrets User` is read-only — the app can get a secret and nothing else.

---

## 6. App settings

Secrets are **references**; everything else is a literal. The app reads them all as ordinary
environment variables and never knows Key Vault exists.

```bash
KVURI=$(az keyvault show --name $KV --query properties.vaultUri -o tsv | sed 's:/*$::')

az webapp config appsettings set --name $APP --resource-group $RG --settings \
  ASPNETCORE_ENVIRONMENT="Production" \
  ConnectionStrings__WidgetWorks="@Microsoft.KeyVault(SecretUri=$KVURI/secrets/ConnectionStrings--WidgetWorks/)" \
  Jwt__SigningKey="@Microsoft.KeyVault(SecretUri=$KVURI/secrets/Jwt--SigningKey/)" \
  Jwt__Issuer="https://$APP.azurewebsites.net" \
  Jwt__Audience="widgetworks-spa" \
  Payments__Provider="Mock" \
  Email__Provider="Dev"
```

Verify every reference resolved — a broken one shows an error instead of `Resolved`:

```bash
az webapp config appsettings list --name $APP --resource-group $RG \
  --query "[?contains(to_string(value),'KeyVault')].{name:name, value:value}" -o table
```

Portal → App Service → Environment variables shows a green tick per working reference. A red cross
almost always means the role assignment in step 5 has not propagated yet; wait a minute.

---

## 7. Deploy the API — publish output only

Use the script. It publishes to a scratch directory, **refuses to continue** if the output contains
anything that must never be public, zips the contents, deploys, and then polls `/health`:

```bash
APP=$APP RG=$RG ./scripts/deploy-api-azure.sh
```

The guard aborts on any `.cs`, `.csproj`, `.env`, `.sln`, `docker-compose*.yml`, or a `.git/`,
`node_modules/`, `src/`, `web/`, `tests/` or `docs/` directory, and it verifies
`WidgetWorks.WebApi.dll` and `appsettings.json` are present before shipping.

**Never use `az webapp up`.** It infers the project from the working directory and pushes the
entire repository into `wwwroot`, where the source, `.env` and git history are served over HTTP.
That is the single most expensive mistake available here, and the script exists to make it
impossible.

Migrations run automatically on start (DbUp), so there is no separate migration step.

If you deploy by hand instead, the only safe shape is: publish to a directory *outside* the repo,
then zip its **contents** — `(cd "$STAGE" && zip -qr ../api.zip .)`. Zipping the folder itself
nests everything one level down and App Service serves nothing.

---

## 8. SPA on Static Web Apps

The API hostname is only known now, and Vite inlines it at **build** time — so build after the API
exists.

```bash
az staticwebapp create --name $SWA --resource-group $RG --location eastus2 --sku Free
```

Static Web Apps is available in a subset of regions; `eastus2` serves `centralus` fine, and the
free tier is fronted by a CDN anyway.

```bash
API_HOST=$(az webapp show --name $APP --resource-group $RG --query defaultHostName -o tsv)
API_URL="https://$API_HOST"
```

**Both** `VITE_*` values must be present at build time. Omitting `VITE_GOOGLE_CLIENT_ID` doesn't
error — `GoogleButton` renders nothing when it's empty, so Google sign-in just silently disappears
from the deployed site. Set it to the same OAuth Web client id the API has in
`Google__ClientId`, or leave it deliberately blank to disable the feature:

```bash
VITE_API_BASE_URL="$API_URL" \
VITE_GOOGLE_CLIENT_ID="${VITE_GOOGLE_CLIENT_ID:-}" \
  npm --prefix web run build
```

### staticwebapp.config.json

`web/public/staticwebapp.config.json` lives in `public/` so Vite copies it verbatim into `dist/`
(anything outside `public/` is never emitted). It does two jobs Static Web Apps cannot do without
it:

- **`navigationFallback`** rewrites unknown paths to `index.html`. Without it every deep link —
  `/widgets/<id>`, `/orders`, `/checkout` — returns 404, because SWA looks for a real file.
- **A Content-Security-Policy that permits Google.** The sign-in button loads
  `accounts.google.com/gsi/client` and renders inside a Google-hosted frame, so the policy must
  allow `accounts.google.com` in `script-src`, `frame-src` and `connect-src`, plus
  `*.googleusercontent.com` in `img-src` for avatars. Under a default policy Google sign-in fails
  silently. `img-src` also allows `picsum.photos`, which serves the placeholder product photos.

`connect-src` ships with a `REPLACE_API_ORIGIN` placeholder — the browser must be allowed to call
the API, and its hostname isn't known until step 4. Substitute it into the built output:

```bash
sed -i "s|https://REPLACE_API_ORIGIN|$API_URL|" web/dist/staticwebapp.config.json
grep -o "connect-src[^;]*" web/dist/staticwebapp.config.json
```

The `grep` should echo your real API origin. If `REPLACE_API_ORIGIN` is still there, every API call
from the deployed SPA will be blocked by CSP.

```bash
TOKEN=$(az staticwebapp secrets list --name $SWA --resource-group $RG \
  --query "properties.apiKey" -o tsv)

npx --yes @azure/static-web-apps-cli deploy ./web/dist --deployment-token "$TOKEN" --env production
```

Only `web/dist` is uploaded — the built bundle, never the source tree.

Remember every `VITE_*` value is compiled into the JavaScript and is public. Never put a secret
there; `VITE_GOOGLE_CLIENT_ID` is a public client id and is safe.

---

## 9. Close the CORS loop

The API must name the SPA origin explicitly — no wildcards, because the app sends credentials.

```bash
SWA_URL="https://$(az staticwebapp show --name $SWA --resource-group $RG --query defaultHostname -o tsv)"

az webapp config appsettings set --name $APP --resource-group $RG --settings \
  Cors__AllowedOrigins="$SWA_URL" \
  App__BaseUrl="$SWA_URL"
```

`App__BaseUrl` is what password-reset emails build their links from; leaving it at localhost sends
customers to their own machine.

---

## Every configuration key the app reads

Taken from the source, not from memory — `grep` for `configuration["..."]`, `GetSection` and
`GetConnectionString` across `src/`. Anything absent falls back to the default shown.

| Key | Required? | Default if unset |
|---|---|---|
| `ConnectionStrings__WidgetWorks` | **Yes in Azure** | falls back to `Postgres__*`, i.e. `localhost` → **boot loop** |
| `Postgres__Host` / `Port` / `Database` / `Username` / `Password` | no (Docker path) | `localhost` / `5432` / `widgetworks` / `widgetworks` / empty |
| `Jwt__SigningKey` | **Yes** | empty — the app boots but every token operation fails |
| `Jwt__Issuer` / `Audience` / `KeyId` / `AccessTokenMinutes` / `RefreshTokenDays` | no | `appsettings.json` |
| `Cors__AllowedOrigins` | **Yes** | unset means the SPA is blocked by the browser |
| `App__BaseUrl` | for email links | password-reset links point at localhost |
| `Payments__Provider` | no | `Mock` |
| `Payments__Mock__WebhookSecret` | no | empty = webhook needs no signature |
| `Payments__Stripe__SecretKey` / `WebhookSecret` | only if provider is Stripe | empty |
| `Email__Provider` | no | `Dev` (writes to the log) |
| `Email__Host` / `Port` / `UseStartTls` / `Username` / `Password` / `FromAddress` / `FromName` | only if provider is Smtp | `localhost` / `587` / **`true`** / … |
| `Google__ClientId` | only for Google sign-in | empty = disabled server-side |
| `Seed__DemoAdminEmail` / `DemoCustomerEmail` | no | `appsettings.json` |
| `Seed__DemoAdminPassword` / `DemoCustomerPassword` | no | empty |
| `AccountSecurity` section | no | code defaults |

Only two of these will stop a deployment dead: the connection string (boot loop) and
`Cors__AllowedOrigins` (SPA cannot reach the API). `Jwt__SigningKey` fails later, at first sign-in,
which is easy to misdiagnose as a login bug.

## What ships in the deployment

Verified against a real `dotnet publish`:

- The **11 migrations are embedded resources inside `WidgetWorks.Infrastructure.dll`**, not loose
  files. There is no `Migrations/` folder to copy and none to forget.
- `appsettings.json` ships and is required for the non-secret defaults.
- `appsettings.Development.json`, `web.config` and the `.pdb` files also ship. The first two are
  inert on Linux. The `.pdb`s only add stack-trace detail; add `-p:DebugType=none` to the publish
  if you would rather not ship them.
- The SPA bundle is `index.html`, `assets/` and `staticwebapp.config.json` — the config only
  reaches `dist/` because it lives in `web/public/`.

## 10. Verify

```bash
curl -s "$API_URL/health"
curl -s "$API_URL/catalog/widgets?pageSize=3" | head -c 200
curl -s -o /dev/null -w "SPA %{http_code}\n" "$SWA_URL"
```

Confirm nothing leaked into the site root:

```bash
for p in .env appsettings.json src web .git; do
  printf "%-18s " "$p"
  curl -s -o /dev/null -w "%{http_code}\n" "$API_URL/$p"
done
```

Every one should be `404`. A `200` on any of them means the repository was deployed instead of the
publish output — redo step 7.

---

## Costs

| Resource | Tier | Cost |
|---|---|---|
| App Service plan | F1 Free | $0 |
| Static Web App | Free | $0 |
| Key Vault | Standard | ~$0 (per-transaction; references are cached) |
| PostgreSQL | Neon free | $0 |

F1 has no Always On, so the API sleeps and the first request after idle takes ~30s. Neon adds its
own wake on top. Both are inherent to free tiers.

Set a budget alert regardless — a Pay-As-You-Go subscription with the spending limit off has no
brake:

```bash
az consumption budget list --query "[].{name:name, amount:amount}" -o table
```

## Teardown

```bash
az group delete --name $RG --yes --no-wait
```

Purge protection keeps the vault recoverable (and its name reserved) for the retention window.
