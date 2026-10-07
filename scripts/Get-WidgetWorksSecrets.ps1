<#
.SYNOPSIS
    Reads everything the WidgetWorks deployment workflows need out of Azure — four Actions secrets
    and two Actions variables — and optionally writes them straight into the repo.

.DESCRIPTION
    GitHub secrets are write-only: nothing can read them back, which is why they cannot simply be
    copied between repositories. Azure still knows all of it, so the whole set is recoverable rather
    than lost.

    Secrets (gh secret):

      AZURE_SUBSCRIPTION_ID            the active subscription
      AZURE_TENANT_ID                  the directory behind it
      AZURE_CLIENT_ID                  the identity GitHub signs in as (OIDC, no password)
      AZURE_STATIC_WEB_APPS_API_TOKEN  the Static Web App deployment token  <-- the only real credential

    Variables (gh variable):

      VITE_API_BASE_URL                the API origin the SPA calls
      VITE_GOOGLE_CLIENT_ID            the Google sign-in client id

    The split is not arbitrary and the script treats the two halves differently. The variables are
    baked into the JavaScript bundle at build time, so they are published to every visitor the moment
    the site deploys — they are configuration, not credentials, and masking them here would be
    theatre. The first three secrets are identifiers too: with OIDC there is no password anywhere,
    and signing in requires a token GitHub issues to one specific repository and branch or
    environment, so the client id alone grants nothing.

    Only AZURE_STATIC_WEB_APPS_API_TOKEN is a live credential — anyone holding it can push a site to
    the Static Web App. It is masked by default. -Show prints it and therefore leaves it in your shell
    history and scrollback; prefer -SetInGitHub, which pipes it to gh without displaying it at all.

.EXAMPLE
    .\Get-WidgetWorksSecrets.ps1
    Reports what was found, with the deployment token masked.

.EXAMPLE
    .\Get-WidgetWorksSecrets.ps1 -SetInGitHub
    Writes all six into the repo without printing the deployment token.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-widgetworks',
    [string] $Repo          = 'bgard68/WidgetWorks',

    # Print the deployment token as well. Leaves it in your shell history.
    [switch] $Show,

    # Write the secrets and variables into the repo via gh. Does not print the token.
    [switch] $SetInGitHub
)

$ErrorActionPreference = 'Stop'

function Assert-Tool([string] $Name, [string] $Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found on PATH. $Hint"
    }
}

Assert-Tool 'az' 'Install the Azure CLI, then run: az login'
if ($SetInGitHub) { Assert-Tool 'gh' 'Install the GitHub CLI, then run: gh auth login' }

Write-Host 'Reading from Azure...' -ForegroundColor Cyan

# ---------------------------------------------------------------- subscription + tenant
# These come from the *active* subscription. If you hold more than one — and more than one directory
# — az account show reports whichever happens to be current, which need not be the one hosting
# WidgetWorks. The signed-in account and subscription are echoed below for exactly that reason: two
# subscriptions sharing a display name is normal, and silently reading the wrong one yields four
# plausible-looking values that authenticate against nothing.
$account = az account show --output json 2>$null | ConvertFrom-Json
if (-not $account) { throw 'Not signed in to Azure. Run: az login' }

$subscriptionId = $account.id
$tenantId       = $account.tenantId
Write-Host "  signed in as  : $($account.user.name)"
Write-Host "  subscription  : $($account.name) ($($account.id))"

# ---------------------------------------------------------------- the OIDC identity
# A user-assigned managed identity, not an app registration. That distinction matters more than it
# sounds: the two are listed by different commands, and `az ad app list --all` will never show this
# one however hard you look for it.
#
# Found by its federated credential rather than by name, because the credential is the thing that
# actually ties an identity to this repository — so matching on it cannot pick the wrong identity.
$clientId = $null
$subjects = @()

$identities = az identity list --resource-group $ResourceGroup `
                  --query "[].{name:name, clientId:clientId}" --output json 2>$null | ConvertFrom-Json

foreach ($msi in $identities) {
    $creds = az identity federated-credential list --resource-group $ResourceGroup `
                 --identity-name $msi.name --output json 2>$null | ConvertFrom-Json
    $matched = @($creds | Where-Object { $_.subject -match 'WidgetWorks' })
    if ($matched.Count -gt 0) {
        $clientId = $msi.clientId
        $subjects = $matched
        Write-Host "  identity      : $($msi.name)"
        break
    }
}

if (-not $clientId) {
    Write-Warning "No managed identity in '$ResourceGroup' has a federated credential mentioning WidgetWorks."
    Write-Warning 'AZURE_CLIENT_ID will be blank. Check the resource group, and that you are in the right directory.'
} else {
    # Each subject pins a repository and a branch or environment. Renaming the repository breaks the
    # match, and azure/login then fails with four perfectly correct secrets — which reads as a bad
    # secret and is not one. Printing the subjects saves the next person that hunt.
    $repoName = ($Repo -split '/')[-1]
    foreach ($s in $subjects) {
        $note = if ($s.subject -notmatch [regex]::Escape($repoName)) { '   <-- does not name this repo' } else { '' }
        Write-Host "  credential    : $($s.subject)$note"
    }
}

# ---------------------------------------------------------------- Static Web App deployment token
$swaToken = $null
$swa = az staticwebapp list --resource-group $ResourceGroup --query "[0].name" --output tsv 2>$null
if ($swa) {
    Write-Host "  static web app: $swa"
    $swaToken = az staticwebapp secrets list --name $swa --resource-group $ResourceGroup `
                   --query "properties.apiKey" --output tsv 2>$null
} else {
    Write-Warning "No Static Web App found in resource group '$ResourceGroup'."
    Write-Warning 'Get the token from the portal instead: your Static Web App -> Manage deployment token.'
}

# ---------------------------------------------------------------- the build-time variables
# Derived, not hardcoded. The API origin comes from the App Service's own hostname, and the Google
# client id from the API's app settings — where it has to be anyway, because the API validates the
# `aud` claim on Google's ID tokens against it. Reading it from there rather than keeping a second
# copy in this script is what stops the two drifting apart: a mismatch would let the SPA mint tokens
# the API then refuses, which presents as sign-in failing for no visible reason.
$apiBaseUrl = $null
$apiApp = az webapp list --resource-group $ResourceGroup --query "[0].{name:name, host:defaultHostName}" `
              --output json 2>$null | ConvertFrom-Json
if ($apiApp) {
    $apiBaseUrl = "https://$($apiApp.host)"
    Write-Host "  api           : $($apiApp.name)"
} else {
    Write-Warning "No App Service found in resource group '$ResourceGroup'. VITE_API_BASE_URL will be blank."
}

$googleClientId = $null
if ($apiApp) {
    $googleClientId = az webapp config appsettings list --resource-group $ResourceGroup --name $apiApp.name `
                          --query "[?name=='Google__ClientId'].value | [0]" --output tsv 2>$null
    if (-not $googleClientId) {
        # Blank is a valid state rather than a failure: the sign-in button is hidden when it is unset,
        # which is how the stack runs locally and in CI.
        Write-Warning 'Google__ClientId is not set on the API. VITE_GOOGLE_CLIENT_ID will be blank, which hides the sign-in button.'
    }
}

# ---------------------------------------------------------------- report
$secrets = [ordered]@{
    AZURE_SUBSCRIPTION_ID           = $subscriptionId
    AZURE_TENANT_ID                 = $tenantId
    AZURE_CLIENT_ID                 = $clientId
    AZURE_STATIC_WEB_APPS_API_TOKEN = $swaToken
}

# Public by construction — they ship inside the JavaScript bundle, and gh prints them in plain text
# when you list them. Masking them would only make this script harder to verify.
$variables = [ordered]@{
    VITE_API_BASE_URL     = $apiBaseUrl
    VITE_GOOGLE_CLIENT_ID = $googleClientId
}

function Write-Table([string] $Heading, $Table, [bool] $Mask) {
    Write-Host ''
    Write-Host $Heading -ForegroundColor Cyan
    foreach ($name in $Table.Keys) {
        $value = $Table[$name]
        if (-not $value) {
            Write-Host ('  {0,-32} NOT FOUND' -f $name) -ForegroundColor Red
        } elseif ($Show -or -not $Mask) {
            Write-Host ('  {0,-32} {1}' -f $name, $value) -ForegroundColor Green
        } else {
            # Enough to recognise the value, not enough to use it.
            $masked = if ($value.Length -gt 8) {
                $value.Substring(0, 4) + '...' + $value.Substring($value.Length - 4)
            } else {
                '***'
            }
            Write-Host ('  {0,-32} {1}' -f $name, $masked) -ForegroundColor Green
        }
    }
}

# Masked or not strictly by which side of the secret/variable line a value falls on. Only the
# deployment token is a credential, and three of the four secrets are identifiers that grant nothing
# on their own — but they are still reconnaissance, they are still stored as secrets, and a rule that
# needs a hand-kept exception list is a rule that will eventually be wrong.
Write-Table 'Secrets' $secrets $true
Write-Table 'Variables' $variables $false

if (-not $Show -and -not $SetInGitHub) {
    Write-Host ''
    Write-Host 'Secrets are masked. Re-run with -Show to print them, or -SetInGitHub to write everything' -ForegroundColor Yellow
    Write-Host 'to the repo without printing the deployment token.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- optionally write to GitHub
if ($SetInGitHub) {
    Write-Host ''
    Write-Host "Writing to $Repo ..." -ForegroundColor Cyan

    foreach ($entry in @(
        @{ Table = $secrets;   Verb = 'secret' },
        @{ Table = $variables; Verb = 'variable' }
    )) {
        foreach ($name in $entry.Table.Keys) {
            $value = $entry.Table[$name]
            if (-not $value) {
                Write-Host ('  {0,-32} skipped (not found)' -f $name) -ForegroundColor Red
                continue
            }

            # Piped to stdin rather than passed as an argument, so the value never reaches the
            # process list or PowerShell's command history. Variables do not need that protection,
            # but there is no reason to write them a second way.
            $value | gh $entry.Verb set $name --repo $Repo
            Write-Host ('  {0,-32} set ({1})' -f $name, $entry.Verb) -ForegroundColor Green
        }
    }
}
