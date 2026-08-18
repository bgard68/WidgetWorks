#!/usr/bin/env bash
#
# deploy-api-azure.sh — publish the WebApi and deploy ONLY that to Azure App Service.
#
# Exists because the obvious commands are the dangerous ones. `az webapp up`, or a zip taken from
# the repository root, push the whole tree into wwwroot: .cs sources, .env, .git history,
# node_modules — all served over HTTP. This script publishes to a scratch directory, refuses to
# continue if anything that should never ship is in there, and deploys the verified archive.
#
# Usage:
#   APP=<webapp-name> RG=<resource-group> ./scripts/deploy-api-azure.sh
#   APP=... RG=... SKIP_HEALTH=1 ./scripts/deploy-api-azure.sh    # don't poll /health afterwards
#
set -euo pipefail

APP="${APP:?set APP to the web app name}"
RG="${RG:?set RG to the resource group}"
PROJECT="${PROJECT:-src/WidgetWorks.WebApi/WidgetWorks.WebApi.csproj}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

STAGE="$(mktemp -d)"
ZIP="$(mktemp -u).zip"
cleanup() { rm -rf "$STAGE" "$ZIP"; }
trap cleanup EXIT

log() { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
ok()  { printf '\033[1;32m  ✓\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m  ✗ %s\033[0m\n' "$*" >&2; exit 1; }

# ---- 1. Publish to a scratch directory, never into the repo -------------------
log "Publishing $PROJECT"
dotnet publish "$PROJECT" -c Release -o "$STAGE" --nologo -v q
ok "Published to a temporary directory"

# ---- 2. Refuse to ship anything that should never be public ------------------
# The check is on the staged output, so it catches a mis-typed -o as well as a bad zip root.
log "Auditing the publish output"
FORBIDDEN=0
while IFS= read -r found; do
  printf '\033[1;31m  ✗ %s\033[0m\n' "$found"
  FORBIDDEN=1
done < <(find "$STAGE" \
  \( -name '*.cs' -o -name '*.csproj' -o -name '.env' -o -name '.env.*' \
     -o -name '*.sln' -o -name '*.slnx' -o -name 'docker-compose*.yml' \) -print 2>/dev/null)

for dir in .git node_modules src web tests docs; do
  [[ -e "$STAGE/$dir" ]] && { printf '\033[1;31m  ✗ %s/ present\033[0m\n' "$dir"; FORBIDDEN=1; }
done

[[ "$FORBIDDEN" -eq 1 ]] && die "Publish output contains files that must not be deployed. Aborting."

[[ -f "$STAGE/WidgetWorks.WebApi.dll" ]] || die "WidgetWorks.WebApi.dll missing — publish did not produce a runnable app."
[[ -f "$STAGE/appsettings.json" ]]       || die "appsettings.json missing — the app needs its non-secret defaults."
ok "Only build output present ($(find "$STAGE" -type f | wc -l | tr -d ' ') files)"

# ---- 3. Zip the CONTENTS, not the folder -------------------------------------
# A zip of the folder itself nests everything one level down and App Service serves nothing.
log "Packaging"
(cd "$STAGE" && zip -qr "$ZIP" .)
ok "Archive built"

# ---- 4. Deploy ---------------------------------------------------------------
log "Deploying to $APP"
az webapp deploy --name "$APP" --resource-group "$RG" --type zip --src-path "$ZIP" --output none
ok "Deployed"

# ---- 5. Confirm it actually came up ------------------------------------------
# /health returns 503 with a reason when the database is unreachable, rather than the app
# crash-looping. Surfacing that here is the difference between a two-minute fix and a lost day
# of a free tier's CPU quota.
if [[ "${SKIP_HEALTH:-0}" != "1" ]]; then
  HOST="$(az webapp show --name "$APP" --resource-group "$RG" --query defaultHostName -o tsv)"
  log "Waiting for https://$HOST/health"
  for i in $(seq 1 20); do
    BODY="$(curl -s --max-time 20 "https://$HOST/health" || true)"
    CODE="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "https://$HOST/health" || true)"
    case "$CODE" in
      200) ok "Healthy: $BODY"; exit 0 ;;
      503) printf '\033[1;31m  ✗ Unhealthy: %s\033[0m\n' "$BODY"
           echo   "    The app is UP but the database is not reachable. Fix the setting, then:"
           echo   "      az webapp restart --name $APP --resource-group $RG"
           echo   "    To stop consuming CPU quota while you investigate:"
           echo   "      az webapp stop --name $APP --resource-group $RG"
           exit 1 ;;
      *)   printf '  … %s (attempt %s/20)\n' "${CODE:-no response}" "$i"; sleep 6 ;;
    esac
  done
  die "No healthy response after ~2 minutes. Check: az webapp log tail --name $APP --resource-group $RG"
fi
