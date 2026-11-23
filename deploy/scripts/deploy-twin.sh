#!/usr/bin/env bash
# Deploys one twin release candidate: waits out a running rebuild, deploys infra/main.bicep with the image,
# then waits until the new revision is ready and serving VERSION.
# TWIN_WHAT_IF=true is the gate used before the rollout: the image must be in the registry and a Bicep what-if of the
# release must delete nothing; nothing is deployed.
# Used by GitHub Actions (ci-deploy.yml) and by the Octopus process in deploy/octopus/terraform.
# Expects an Azure CLI session (deploy/scripts/azure-login.sh). Secrets come from the environment only and are never echoed.
set -euo pipefail

: "${IMAGE:?IMAGE is required (registry/repository:tag)}"
: "${VERSION:?VERSION is required (the release candidate version the new revision must serve)}"
: "${ARM_CLIENT_ID:?}" "${ARM_CLIENT_SECRET:?}" "${ARM_TENANT_ID:?}" "${ARM_SUBSCRIPTION_ID:?}"
: "${OCTOPUS_URL:?}" "${OCTOPUS_API_KEY:?}" "${GH_PAT:?}" "${WEBHOOK_SECRET:?}" "${TWIN_API_KEY:?}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLUG="${TWIN_SYSTEM_SLUG:-cmdemo1}"
RG="${TWIN_RESOURCE_GROUP:-rg-${SLUG}-nonprod}"
APP="${TWIN_APP_NAME:-ca-${SLUG}-twin}"
ACR_NAME="${ACR_NAME:-acrcmdemo1chu4gq}"
ACR_RG="${ACR_RESOURCE_GROUP:-$RG}"
DEPLOYMENT_NAME="${TWIN_DEPLOYMENT_NAME:-deploy-${SLUG}-twin}"
# Another platform: TWIN_SYSTEM_SLUG names the twin's resources, TWIN_PLATFORM_FILE is its *.platform.json (docs/setup/README.md).
PLATFORM_JSON=""
if [ -n "${TWIN_PLATFORM_FILE:-}" ]; then PLATFORM_JSON=$(grep -vE '^[[:space:]]*//' "$TWIN_PLATFORM_FILE" | jq -c .); fi
# The Octopus package ships the compiled ARM template, so workers need no Bicep CLI; a checkout uses the Bicep source.
if [ -z "${TEMPLATE_FILE:-}" ]; then
  if [ -f "$ROOT/infra/main.json" ]; then TEMPLATE_FILE="$ROOT/infra/main.json"; else TEMPLATE_FILE="$ROOT/infra/main.bicep"; fi
fi
mask() { if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::add-mask::$1"; fi; }

WHAT_IF="${TWIN_WHAT_IF:-false}"
if [ "$WHAT_IF" = "true" ]; then
  echo "== Gate: ${IMAGE} is in ${ACR_NAME}"
  az acr repository show --name "$ACR_NAME" --image "${IMAGE#*/}" --query "{tag: name, createdTime: createdTime}" -o tsv
fi

if [ "$WHAT_IF" != "true" ]; then
echo "== Deferring while a twin rebuild is running"
# Restarting mid-rebuild would discard its shadow tables (live state is safe, but the work is lost).
FQDN=$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv 2>/dev/null || true)
if [ -n "$FQDN" ]; then
  for i in $(seq 1 120); do
    RUNNING=$(curl -fsS -m 20 -H "X-Twin-Key: ${TWIN_API_KEY}" "https://${FQDN}/api/admin/rebuild/status" | jq -r '.running' 2>/dev/null || echo unknown)
    if [ "$RUNNING" != "true" ]; then echo "No rebuild running (${RUNNING}); deploying."; break; fi
    if [ "$i" -eq 120 ]; then echo "A rebuild has been running for an hour; deploy blocked. Cancel it with POST /api/admin/rebuild/cancel or wait." >&2; exit 1; fi
    [ $((i % 4)) -eq 1 ] && curl -fsS -m 20 -H "X-Twin-Key: ${TWIN_API_KEY}" "https://${FQDN}/api/admin/rebuild/status" | jq -c '{running, phase, rawEventsReplayedSoFar}'
    sleep 30
  done
else
  echo "Twin not deployed yet."
fi
fi

echo "== Deploying ${TEMPLATE_FILE##*/} to ${RG} with ${IMAGE}"
if [ "$WHAT_IF" != "true" ]; then az acr update --name "$ACR_NAME" --resource-group "$ACR_RG" --admin-enabled true --output none; fi
ACR_USER=$(az acr credential show --name "$ACR_NAME" --resource-group "$ACR_RG" --query username -o tsv)
ACR_PASS=$(az acr credential show --name "$ACR_NAME" --resource-group "$ACR_RG" --query 'passwords[0].value' -o tsv)
mask "$ACR_PASS"
DEPLOYER_OID=$(az ad sp show --id "$ARM_CLIENT_ID" --query id -o tsv)
KV_EXISTING=$(az keyvault list -g "$RG" --query "[?tags.purpose=='digital-twin'].name | [0]" -o tsv 2>/dev/null || echo "")
SQL_PASS=""
if [ -n "$KV_EXISTING" ]; then
  SQL_PASS=$(az keyvault secret show --vault-name "$KV_EXISTING" --name sql-admin-password --query value -o tsv 2>/dev/null || echo "")
fi
if [ -z "$SQL_PASS" ]; then
  SQL_PASS=$(python3 -c 'import secrets,string; chars=string.ascii_letters+string.digits+"-_.~"; print("Tw1!"+"".join(secrets.choice(chars) for _ in range(28)))')
fi
mask "$SQL_PASS"

PARAMS_FILE=$(mktemp)
chmod 600 "$PARAMS_FILE"
trap 'rm -f "$PARAMS_FILE"' EXIT
jq -n \
  --arg img "$IMAGE" --arg acrUser "$ACR_USER" --arg acrPass "$ACR_PASS" --arg oid "$DEPLOYER_OID" --arg sqlPass "$SQL_PASS" \
  --arg azCid "$ARM_CLIENT_ID" --arg azSec "$ARM_CLIENT_SECRET" --arg azTid "$ARM_TENANT_ID" --arg azSub "$ARM_SUBSCRIPTION_ID" \
  --arg octoUrl "$OCTOPUS_URL" --arg octoKey "$OCTOPUS_API_KEY" --arg ghPat "$GH_PAT" --arg whSec "$WEBHOOK_SECRET" --arg apiKey "$TWIN_API_KEY" --arg slug "$SLUG" --arg platformJson "$PLATFORM_JSON" \
  '{
    "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
    "contentVersion": "1.0.0.0",
    "parameters": {
      "containerImage": { "value": $img },
      "acrUsername": { "value": $acrUser },
      "acrPassword": { "value": $acrPass },
      "deployerObjectId": { "value": $oid },
      "sqlAdminPassword": { "value": $sqlPass },
      "azureClientId": { "value": $azCid },
      "azureClientSecret": { "value": $azSec },
      "azureTenantId": { "value": $azTid },
      "azureSubscriptionId": { "value": $azSub },
      "octopusUrl": { "value": $octoUrl },
      "octopusApiKey": { "value": $octoKey },
      "gitHubPat": { "value": $ghPat },
      "webhookSecret": { "value": $whSec },
      "twinApiKey": { "value": $apiKey },
      "systemSlug": { "value": $slug },
      "platformJson": { "value": $platformJson }
    }
  }' > "$PARAMS_FILE"
# TWIN_INTAKE_TRANSPORT=SqlServer is the fallback switch off Service Bus (the namespace stays); empty keeps the Bicep default.
if [ -n "${TWIN_INTAKE_TRANSPORT:-}" ]; then
  jq --arg t "$TWIN_INTAKE_TRANSPORT" '.parameters.intakeTransport = { value: $t }' "$PARAMS_FILE" > "${PARAMS_FILE}.tmp" && mv "${PARAMS_FILE}.tmp" "$PARAMS_FILE"
  chmod 600 "$PARAMS_FILE"
fi

if [ "$WHAT_IF" = "true" ]; then
  echo "== Gate: Bicep what-if for ${VERSION}"
  CHANGES=$(az deployment group what-if --resource-group "$RG" --name "$DEPLOYMENT_NAME" --template-file "$TEMPLATE_FILE" \
    --parameters "@${PARAMS_FILE}" --no-pretty-print --query "changes[].[changeType, resourceId]" -o tsv)
  echo "$CHANGES" | cut -f1 | sort | uniq -c
  echo "$CHANGES" | awk -F'\t' '$1 != "Ignore" && $1 != "NoChange" { print "  " $1 ": " $2 }' | sed -E 's#/subscriptions/[^/]+#/subscriptions/<sub>#'
  if echo "$CHANGES" | grep -q '^Delete'; then echo "What-if would delete resources; refusing to promote ${VERSION}" >&2; exit 1; fi
  FQDN=$(az containerapp show -n "$APP" -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)
  if [ -n "${TWIN_OUTPUT_FILE:-}" ]; then echo "https://${FQDN}" > "$TWIN_OUTPUT_FILE"; fi
  echo "Gate passed for ${VERSION}"
  exit 0
fi

TWIN_URL=$(az deployment group create \
  --resource-group "$RG" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$TEMPLATE_FILE" \
  --parameters "@${PARAMS_FILE}" \
  --query properties.outputs.twinUrl.value \
  -o tsv)
echo "Deployed Digital Twin at ${TWIN_URL}"

echo "== Waiting for the new revision to be ready and serving ${VERSION}"
az extension add --name containerapp --upgrade --only-show-errors --output none
READY_OK=false
for i in $(seq 1 60); do
  LATEST=$(az containerapp show -g "$RG" -n "$APP" --query properties.latestRevisionName -o tsv)
  READY=$(az containerapp show -g "$RG" -n "$APP" --query properties.latestReadyRevisionName -o tsv)
  SERVED=$(curl -fsS "${TWIN_URL}/health" 2>/dev/null | jq -r '.version // "unknown"')
  echo "$(date -u +%T) latest=${LATEST} ready=${READY} served=${SERVED} expected=${VERSION}"
  if [ "$LATEST" = "$READY" ] && [ "$SERVED" = "$VERSION" ]; then READY_OK=true; break; fi
  sleep 10
done
if [ "$READY_OK" != "true" ]; then
  echo "Revision ${LATEST} did not become ready serving ${VERSION}" >&2
  az containerapp revision list -g "$RG" -n "$APP" --query "[?properties.active].{name:name, health:properties.healthState, running:properties.runningState, image:properties.template.containers[0].image}" -o table
  az containerapp logs show -g "$RG" -n "$APP" --revision "$LATEST" --type console --tail 80 --format text 2>&1 \
    | grep -viE "password|secret|pwd=|key=" | grep -E "fail:|crit:|Exception|Unhandled|Timeout" | head -40 || true
  exit 1
fi
echo "Revision ${LATEST} is ready and serving ${VERSION}"

if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "twin_url=${TWIN_URL}" >> "$GITHUB_OUTPUT"; fi
if [ -n "${TWIN_OUTPUT_FILE:-}" ]; then echo "$TWIN_URL" > "$TWIN_OUTPUT_FILE"; fi
