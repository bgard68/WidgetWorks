<#
.SYNOPSIS
    Provisions the WidgetWorks stack on Azure free tiers and deploys it.

.DESCRIPTION
    Creates a resource group, Key Vault, Linux App Service plan (F1 Free) and web app, wires the
    web app's managed identity to the vault, publishes a RELEASE build of the API, and deploys the
    SPA to a Free Static Web App.

    Every value is QUERIED, never hard-coded: subscription id, the managed identity's principal id,
    the vault URI, the API hostname, the Static Web App hostname and its deployment token all come
    from `az ... --query`. The script is idempotent — re-running converges rather than erroring.

    Three things it is deliberate about:

      * It never deploys the repository. The API is published to a scratch directory outside the
        repo and AUDITED before packaging; any .cs, .csproj, .env, .sln, compose file or
        src/web/tests/docs/.git directory in the output aborts the run. A zip taken from the repo
        root would serve source, .env and git history out of wwwroot.

      * It builds Release, not Debug, and asserts the app dll and appsettings.json are present
        before shipping — a publish that silently produced nothing would otherwise deploy an empty
        site.

      * It watches the first boot and STOPS the app if it comes back unhealthy. On the F1 tier a
        crash-looping app burns the 60 CPU-minutes/day allowance in silence; a stopped app burns
        nothing. (The API itself also degrades to a 503 rather than exiting — see MigrationRunner.)

.PARAMETER WhatIf
    Print the planned configuration and exit without creating anything.

.EXAMPLE
    ./infra/Provision.ps1 -WhatIf
    ./infra/Provision.ps1
    ./infra/Provision.ps1 -SkipInfra        # redeploy code only
#>
[CmdletBinding()]
param(
    [string] $Project        = 'widgetworks',
    [string] $Location       = 'centralus',
    [string] $ResourceGroup  = 'rg-widgetworks',
    [string] $Sku            = 'F1',
    [string] $SwaLocation    = 'eastus2',      # Static Web Apps runs in a subset of regions
    [string] $GoogleClientId = '',             # public OAuth client id; blank hides the button
    [switch] $SkipInfra,
    [switch] $SkipSecrets,
    [switch] $SkipDeploy,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

function Write-Step { param($m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok   { param($m) Write-Host "  OK  $m"  -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  !   $m"  -ForegroundColor Yellow }
function Stop-With  { param($m) Write-Host "  X   $m"  -ForegroundColor Red; Pop-Location; exit 1 }

# Applies app settings from a JSON file rather than as --settings arguments.
# On Windows az runs through a cmd batch shim, and a value containing parentheses — every
# @Microsoft.KeyVault(SecretUri=...) reference — makes batch treat them as syntax and fail with
# "Jwt__SigningKey was unexpected at this time". A file sidesteps shell quoting entirely.
function Set-AppSettings {
    param([hashtable] $Settings, [string] $App, [string] $Group)
    $file = Join-Path ([System.IO.Path]::GetTempPath()) "ww-settings-$([guid]::NewGuid()).json"
    try {
        $payload = @($Settings.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = $_.Value } })
        # -Compress and an explicit array keep the shape az expects: [{name,value},...]
        [System.IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject $payload -Depth 4))
        $out = & az webapp config appsettings set --name $App --resource-group $Group --settings "@$file" --output none 2>&1
        if ($LASTEXITCODE -ne 0) { Stop-With "app settings failed`n$out" }
    }
    finally { if (Test-Path $file) { Remove-Item -LiteralPath $file -Force } }
}

# Runs az and fails loudly instead of silently continuing with an empty value.
# No param block on purpose: a declared parameter turns this into an advanced function, whose
# common parameters then swallow az's own short switches (-o binds to -OutVariable). Everything
# lands in the automatic $args and is splatted through untouched.
function Invoke-Az {
    $out = & az @args 2>&1
    if ($LASTEXITCODE -ne 0) { Stop-With "az $($args -join ' ')`n$out" }
    return $out
}

# ---------------------------------------------------------------- preflight --
Write-Step 'Preflight'
foreach ($tool in 'az', 'dotnet', 'npm') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-With "$tool not found on PATH." }
}
try { $null = & az account show 2>&1; if ($LASTEXITCODE -ne 0) { throw } }
catch { Stop-With "Not signed in. Run: az login" }

$SubscriptionId = (Invoke-Az account show --query id -o tsv)
$Suffix         = $SubscriptionId.Substring(0, 6)
$VaultName      = "$Project-kv-$Suffix"        # vault names are globally unique
$AppName        = "$Project-api-$Suffix"       # app names are globally unique
$PlanName       = "plan-$Project-free"
$SwaName        = "$Project-web"

Write-Host @"

Planned deployment
------------------------------------------------------------
  Subscription    : $SubscriptionId
  Resource group  : $ResourceGroup ($Location)
  Key Vault       : $VaultName
  App Service     : $AppName  (plan $PlanName, $Sku Linux, DOTNETCORE:10.0)
  Static Web App  : $SwaName  ($SwaLocation, Free)
  Google client id: $(if ($GoogleClientId) { 'set' } else { '(blank - sign-in hidden)' })
------------------------------------------------------------
"@

if ($WhatIf) { Write-Ok 'WhatIf - nothing created.'; Pop-Location; exit 0 }

# --------------------------------------------------------------- 1. group ----
if (-not $SkipInfra) {
    Write-Step "Resource group '$ResourceGroup'"
    Invoke-Az group create --name $ResourceGroup --location $Location --output none | Out-Null
    Write-Ok 'Ready'

    # ------------------------------------------------------------ 2. vault --
    # RBAC rather than legacy access policies, and purge protection so a mistake inside the
    # retention window cannot destroy the secrets outright.
    # `az keyvault create` errors on an existing vault rather than upserting, so check first —
    # otherwise a second run of this script dies here instead of converging.
    Write-Step "Key Vault '$VaultName'"
    $null = & az keyvault show --name $VaultName --resource-group $ResourceGroup --output none 2>&1
    if ($LASTEXITCODE -ne 0) {
        Invoke-Az keyvault create --name $VaultName --resource-group $ResourceGroup --location $Location `
            --enable-rbac-authorization true --enable-purge-protection true --retention-days 90 `
            --output none | Out-Null
    } else {
        Write-Host '  (already exists)'
    }
    $VaultId  = (Invoke-Az keyvault show --name $VaultName --query id -o tsv)
    $VaultUri = ((Invoke-Az keyvault show --name $VaultName --query properties.vaultUri -o tsv)).TrimEnd('/')

    $Me = (Invoke-Az ad signed-in-user show --query id -o tsv)
    & az role assignment create --role 'Key Vault Secrets Officer' --assignee-object-id $Me `
        --assignee-principal-type User --scope $VaultId --output none 2>&1 | Out-Null
    Write-Ok 'Vault ready (RBAC, purge protection, you have Secrets Officer)'

    # ---------------------------------------------------------- 3. secrets --
    # Written from a temp file so no value reaches the command line or shell history.
    $secretFile = Join-Path ([System.IO.Path]::GetTempPath()) "ww-$([guid]::NewGuid()).tmp"
    try {
        $existing = (& az keyvault secret list --vault-name $VaultName --query "[].name" -o tsv 2>$null)

        if ($SkipSecrets) {
            Write-Warn 'SkipSecrets - not writing Jwt--SigningKey.'
        }
        elseif ($existing -notcontains 'Jwt--SigningKey') {
            Write-Step 'Generating Jwt--SigningKey'
            $bytes = New-Object byte[] 48
            [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            [System.IO.File]::WriteAllText($secretFile, [Convert]::ToBase64String($bytes))
            Invoke-Az keyvault secret set --vault-name $VaultName --name 'Jwt--SigningKey' `
                --file $secretFile --output none | Out-Null
            Write-Ok 'Stored (never displayed)'
        } else { Write-Ok 'Jwt--SigningKey already present' }

        if ($SkipSecrets) {
            Write-Warn 'SkipSecrets - the vault may be missing secrets; the app will report 503 until they exist.'
        }
        elseif ($existing -notcontains 'ConnectionStrings--WidgetWorks') {
            Write-Step 'ConnectionStrings--WidgetWorks'
            # An env var makes this runnable unattended (CI, or a non-interactive shell); otherwise
            # prompt, so the value is never an argument and never reaches shell history either way.
            $plain = $env:WW_NEON_CONNECTION_STRING
            if ([string]::IsNullOrWhiteSpace($plain)) {
                Write-Host '  Paste the Neon connection string (input hidden), or set WW_NEON_CONNECTION_STRING and re-run.'
                Write-Host '  Host=...neon.tech;Database=neondb;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true;Timeout=30;Command Timeout=60'
                $secure = Read-Host -AsSecureString '  Connection string'
                $plain  = [System.Net.NetworkCredential]::new('', $secure).Password
            }
            if ([string]::IsNullOrWhiteSpace($plain)) { Stop-With 'Empty connection string.' }
            # Timeouts keep a cold Neon wake from failing DbUp and parking the app unhealthy.
            if ($plain -notmatch 'Timeout') { Write-Warn 'No Timeout= present; a cold Neon start may fail the first boot.' }
            [System.IO.File]::WriteAllText($secretFile, $plain)
            Invoke-Az keyvault secret set --vault-name $VaultName --name 'ConnectionStrings--WidgetWorks' `
                --file $secretFile --output none | Out-Null
            Remove-Variable plain
            Write-Ok 'Stored'
        } else { Write-Ok 'ConnectionStrings--WidgetWorks already present' }
    }
    finally {
        if (Test-Path $secretFile) { Remove-Item $secretFile -Force }
    }

    # ------------------------------------------------------ 4. plan + app ----
    Write-Step "App Service plan '$PlanName' ($Sku Linux)"
    $null = & az appservice plan show --name $PlanName --resource-group $ResourceGroup --output none 2>&1
    if ($LASTEXITCODE -ne 0) {
        Invoke-Az appservice plan create --name $PlanName --resource-group $ResourceGroup `
            --location $Location --is-linux --sku $Sku --output none | Out-Null
    } else { Write-Host '  (already exists)' }

    Write-Step "Web app '$AppName'"
    $null = & az webapp show --name $AppName --resource-group $ResourceGroup --output none 2>&1
    if ($LASTEXITCODE -ne 0) {
        Invoke-Az webapp create --name $AppName --resource-group $ResourceGroup --plan $PlanName `
            --runtime 'DOTNETCORE:10.0' --output none | Out-Null
    } else { Write-Host '  (already exists)' }

    # https only, TLS 1.2, and FTPS off - the plaintext publish channel is the most-missed hole.
    Invoke-Az webapp update --name $AppName --resource-group $ResourceGroup --https-only true --output none | Out-Null
    Invoke-Az webapp config set --name $AppName --resource-group $ResourceGroup `
        --min-tls-version 1.2 --ftps-state Disabled --http20-enabled true --output none | Out-Null
    Write-Ok 'Created and hardened'

    # -------------------------------------------------- 5. identity + RBAC ---
    Write-Step 'Managed identity to Key Vault'
    $PrincipalId = (Invoke-Az webapp identity assign --name $AppName --resource-group $ResourceGroup `
        --query principalId -o tsv)
    & az role assignment create --role 'Key Vault Secrets User' --assignee-object-id $PrincipalId `
        --assignee-principal-type ServicePrincipal --scope $VaultId --output none 2>&1 | Out-Null
    Write-Ok "Granted Key Vault Secrets User (read-only) to $PrincipalId"

    # ------------------------------------------------------ 6. app settings --
    # Secrets are references resolved at runtime by the identity above; nothing secret is stored here.
    Write-Step 'App settings'
    Set-AppSettings -App $AppName -Group $ResourceGroup -Settings @{
        'ASPNETCORE_ENVIRONMENT'          = 'Production'
        'ConnectionStrings__WidgetWorks'  = "@Microsoft.KeyVault(SecretUri=$VaultUri/secrets/ConnectionStrings--WidgetWorks/)"
        'Jwt__SigningKey'                 = "@Microsoft.KeyVault(SecretUri=$VaultUri/secrets/Jwt--SigningKey/)"
        'Jwt__Issuer'                     = "https://$AppName.azurewebsites.net"
        'Jwt__Audience'                   = 'widgetworks-spa'
        'Payments__Provider'              = 'Mock'
        'Email__Provider'                 = 'Dev'
    }
    Write-Ok 'Set (secrets are Key Vault references, not values)'
}

$ApiHost = (Invoke-Az webapp show --name $AppName --resource-group $ResourceGroup --query defaultHostName -o tsv)
$ApiUrl  = "https://$ApiHost"

if ($SkipDeploy) { Write-Ok "Infra only. API: $ApiUrl"; Pop-Location; exit 0 }

# ------------------------------------------------- 7. publish + audit API ----
Write-Step 'Publishing the API (Release)'
$Stage = Join-Path ([System.IO.Path]::GetTempPath()) "ww-publish-$([guid]::NewGuid())"
$Zip   = Join-Path ([System.IO.Path]::GetTempPath()) "ww-api-$([guid]::NewGuid()).zip"
try {
    & dotnet publish 'src/WidgetWorks.WebApi/WidgetWorks.WebApi.csproj' -c Release -o $Stage --nologo -v q
    if ($LASTEXITCODE -ne 0) { Stop-With 'dotnet publish failed.' }
    Write-Ok 'Published to a scratch directory outside the repo'

    # The guard. Anything here that is not build output means the repo is about to be served.
    Write-Step 'Auditing the publish output'
    $bad = @()
    $bad += Get-ChildItem $Stage -Recurse -File -Include '*.cs','*.csproj','*.sln','*.slnx','.env','.env.*','docker-compose*.yml' -ErrorAction SilentlyContinue
    foreach ($d in '.git', 'node_modules', 'src', 'web', 'tests', 'docs') {
        $p = Join-Path $Stage $d
        if (Test-Path $p) { $bad += Get-Item $p }
    }
    if ($bad.Count -gt 0) {
        $bad | ForEach-Object { Write-Host "  X   $($_.Name)" -ForegroundColor Red }
        Stop-With 'Publish output contains files that must never be deployed. Aborting.'
    }

    foreach ($required in 'WidgetWorks.WebApi.dll', 'appsettings.json') {
        if (-not (Test-Path (Join-Path $Stage $required))) { Stop-With "$required missing - publish produced no runnable app." }
    }
    $fileCount = (Get-ChildItem $Stage -Recurse -File).Count
    Write-Ok "Build output only ($fileCount files); app dll and appsettings.json present"

    # Zip the CONTENTS. Zipping the folder nests everything a level down and serves nothing.
    Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $Zip -Force
    Write-Step "Deploying to $AppName"
    Invoke-Az webapp deploy --name $AppName --resource-group $ResourceGroup --type zip --src-path $Zip --output none | Out-Null
    Write-Ok 'Deployed'
}
finally {
    if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
    if (Test-Path $Zip)   { Remove-Item $Zip -Force }
}

# ------------------------------------------------------------ 8. health ------
# The quota guard. A 503 means the app is up but the database is unreachable; stopping it
# immediately costs nothing, whereas leaving it to restart chews through the F1 daily allowance.
Write-Step "Waiting for $ApiUrl/health"
$healthy = $false
foreach ($i in 1..20) {
    try {
        $r = Invoke-WebRequest "$ApiUrl/health" -TimeoutSec 20 -SkipHttpErrorCheck
        if ($r.StatusCode -eq 200) { Write-Ok "Healthy: $($r.Content)"; $healthy = $true; break }
        if ($r.StatusCode -eq 503) {
            Write-Host "  X   Unhealthy: $($r.Content)" -ForegroundColor Red
            Write-Warn 'Stopping the app so it cannot consume CPU quota while you investigate.'
            Invoke-Az webapp stop --name $AppName --resource-group $ResourceGroup --output none | Out-Null
            Write-Host "      Fix ConnectionStrings--WidgetWorks in the vault, then:" -ForegroundColor Yellow
            Write-Host "        az webapp start --name $AppName --resource-group $ResourceGroup" -ForegroundColor Yellow
            Stop-With 'Deployment succeeded but the app cannot reach its database.'
        }
        Write-Host "  ... $($r.StatusCode) (attempt $i/20)"
    } catch { Write-Host "  ... no response yet (attempt $i/20)" }
    Start-Sleep -Seconds 6
}
if (-not $healthy) {
    Write-Warn 'No healthy response after ~2 minutes. Stopping the app to protect quota.'
    Invoke-Az webapp stop --name $AppName --resource-group $ResourceGroup --output none | Out-Null
    Stop-With "Check: az webapp log tail --name $AppName --resource-group $ResourceGroup"
}

# ----------------------------------------------------------- 9. SPA + SWA ----
Write-Step "Static Web App '$SwaName'"
Invoke-Az staticwebapp create --name $SwaName --resource-group $ResourceGroup `
    --location $SwaLocation --sku Free --output none | Out-Null

# Vite inlines these at BUILD time, so the API must already exist. Both are public values.
Write-Step 'Building the SPA (production)'
$env:VITE_API_BASE_URL    = $ApiUrl
$env:VITE_GOOGLE_CLIENT_ID = $GoogleClientId
& npm --prefix web run build
if ($LASTEXITCODE -ne 0) { Stop-With 'SPA build failed.' }

# staticwebapp.config.json ships with a placeholder because the API hostname is unknown until now.
# Without this substitution the CSP blocks every call the SPA makes to its own API.
$cfg = 'web/dist/staticwebapp.config.json'
if (-not (Test-Path $cfg)) { Stop-With "$cfg missing - it must live in web/public/ so Vite emits it." }
(Get-Content $cfg -Raw).Replace('https://REPLACE_API_ORIGIN', $ApiUrl) | Set-Content $cfg -NoNewline
if ((Get-Content $cfg -Raw) -match 'REPLACE_API_ORIGIN') { Stop-With 'CSP placeholder not substituted.' }
Write-Ok 'Built; CSP now allows the API origin and accounts.google.com'

$SwaToken = (Invoke-Az staticwebapp secrets list --name $SwaName --resource-group $ResourceGroup `
    --query properties.apiKey -o tsv)
& npx --yes @azure/static-web-apps-cli deploy ./web/dist --deployment-token $SwaToken --env production
if ($LASTEXITCODE -ne 0) { Stop-With 'SPA deploy failed.' }

$SwaHost = (Invoke-Az staticwebapp show --name $SwaName --resource-group $ResourceGroup --query defaultHostname -o tsv)
$SwaUrl  = "https://$SwaHost"
Write-Ok "SPA deployed to $SwaUrl"

# ---------------------------------------------------------- 10. CORS loop ----
# Named origin, never a wildcard - the app sends credentials. App__BaseUrl is what password-reset
# emails build their links from; left at localhost it sends customers to their own machine.
Write-Step 'Wiring CORS and email links'
Set-AppSettings -App $AppName -Group $ResourceGroup -Settings @{
    'Cors__AllowedOrigins' = $SwaUrl
    'App__BaseUrl'         = $SwaUrl
}
Write-Ok 'Set'

Write-Host @"

Done
------------------------------------------------------------
  API   : $ApiUrl
  SPA   : $SwaUrl
  Vault : $VaultName
  Group : $ResourceGroup

  Teardown : az group delete --name $ResourceGroup --yes --no-wait
  Stop API : az webapp stop --name $AppName --resource-group $ResourceGroup
------------------------------------------------------------
"@ -ForegroundColor Green

Pop-Location
