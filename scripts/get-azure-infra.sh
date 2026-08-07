#!/usr/bin/env bash
###############################################################################
# get-azure-infra.sh
#
# Dynamically PULLS the current Azure infrastructure for WidgetWorks so it can
# be inspected or rebuilt. This SCRIPT is safe to commit: it contains NO
# secrets, keys, or connection strings — it authenticates interactively (or via
# workload-identity/OIDC in CI) at runtime and writes its output to
# infra/exports/, which is git-ignored.
#
# Nothing this script produces should ever be committed. Only the script lives
# in the repo (per the DevSecOps policy: scripts in, exports out).
#
# Usage:
#   az login                       # or OIDC federation in CI (no stored creds)
#   ./scripts/get-azure-infra.sh <resource-group> [subscription-id]
###############################################################################
set -euo pipefail

RG="${1:-}"
SUB="${2:-}"
OUT_DIR="infra/exports"

if [[ -z "$RG" ]]; then
  echo "Usage: $0 <resource-group> [subscription-id]" >&2
  exit 2
fi

command -v az >/dev/null 2>&1 || { echo "Azure CLI (az) is required." >&2; exit 1; }

# Never echo credentials; rely on the ambient az login / OIDC context.
if ! az account show >/dev/null 2>&1; then
  echo "Not logged in. Run 'az login' (or configure OIDC in CI) first." >&2
  exit 1
fi

[[ -n "$SUB" ]] && az account set --subscription "$SUB"

mkdir -p "$OUT_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

echo "Exporting ARM template for resource group '$RG' ..."
az group export --resource-group "$RG" --skip-all-params \
  > "$OUT_DIR/arm-export-${RG}-${STAMP}.json"

echo "Listing resources (inventory) ..."
az resource list --resource-group "$RG" -o json \
  > "$OUT_DIR/resources-${RG}-${STAMP}.json"

echo "Done. Wrote git-ignored exports to $OUT_DIR/ (stamp: $STAMP)."
echo "Reminder: exports may contain sensitive values — do NOT commit them."
