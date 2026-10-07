<#
.SYNOPSIS
    Reads the four GitHub Actions secrets WidgetWorks needs out of Azure, and optionally writes them
    straight into the repo.

.DESCRIPTION
    GitHub secrets are write-only — nothing can read them back, which is why they cannot simply be
    copied between repositories. All four are recoverable from Azure instead:

      AZURE_SUBSCRIPTION_ID            the active subscription
      AZURE_TENANT_ID                  the directory behind it
      AZURE_CLIENT_ID                  the identity GitHub signs in as (OIDC, no password)
      AZURE_STATIC_WEB_APPS_API_TOKEN  the Static Web App deployment token  <-- the only real credential

    The first three are identifiers, not credentials. With OIDC there is no password anywhere: signing
    in requires a token GitHub issues to one specific repository and branch or environment, so the
    client id on its own grants nothing. The fourth is different — it is a live deployment key, and
    anyone holding it can push a site to the Static Web App. -Show prints it, and therefore leaves it
    in your shell history and scrollback. Prefer -SetInGitHub, which pipes it to gh without displaying
    it at all.

.EXAMPLE
    .\Get-WidgetWorksSecrets.ps1
    Lists each secret name with a masked value, so you can see what was found without exposing it.

.EXAMPLE
    .\Get-WidgetWorksSecrets.ps1 -SetInGitHub
    Writes all four into the repo without printing the deployment token.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-widgetworks',
    [string] $Repo          = 'bgard68/WidgetWorks',

    # Print the values, including the deployment token. Leaves them in your shell history.
    [switch] $Show,

    # Write the values into the repo's Actions secrets via gh. Does not print the token.
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
# WidgetWorks. The signed-in account and subscription name are echoed below for exactly that reason:
# two subscriptions sharing a display name is normal, and silently reading the wrong one produces
# four plausible-looking values that authenticate against nothing.
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

# ---------------------------------------------------------------- report
$secrets = [ordered]@{
    AZURE_SUBSCRIPTION_ID           = $subscriptionId
    AZURE_TENANT_ID                 = $tenantId
    AZURE_CLIENT_ID                 = $clientId
    AZURE_STATIC_WEB_APPS_API_TOKEN = $swaToken
}

Write-Host ''
foreach ($name in $secrets.Keys) {
    $value = $secrets[$name]
    if (-not $value) {
        Write-Host ('{0,-32} NOT FOUND' -f $name) -ForegroundColor Red
    } elseif ($Show) {
        Write-Host ('{0,-32} {1}' -f $name, $value) -ForegroundColor Green
    } else {
        # Enough to recognise a value, not enough to use it.
        $masked = if ($value.Length -gt 8) {
            $value.Substring(0, 4) + '...' + $value.Substring($value.Length - 4)
        } else {
            '***'
        }
        Write-Host ('{0,-32} {1}' -f $name, $masked) -ForegroundColor Green
    }
}

if (-not $Show -and -not $SetInGitHub) {
    Write-Host ''
    Write-Host 'Values masked. Re-run with -Show to print them, or -SetInGitHub to write them to the repo.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- optionally write to GitHub
if ($SetInGitHub) {
    Write-Host ''
    Write-Host "Writing to $Repo ..." -ForegroundColor Cyan
    foreach ($name in $secrets.Keys) {
        $value = $secrets[$name]
        if (-not $value) {
            Write-Host ('  {0,-32} skipped (not found)' -f $name) -ForegroundColor Red
            continue
        }

        # Piped to stdin rather than passed as an argument, so the value never reaches the process
        # list or PowerShell's command history.
        $value | gh secret set $name --repo $Repo
        Write-Host ('  {0,-32} set' -f $name) -ForegroundColor Green
    }
}
