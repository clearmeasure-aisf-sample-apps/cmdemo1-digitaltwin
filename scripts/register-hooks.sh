#!/usr/bin/env bash
set -euo pipefail

# Idempotently register:
# 1. GitHub repo webhooks across all active (non-archived) cmdemo1-* repos in clearmeasure-aisf-sample-apps
# 2. Octopus Deploy Subscription in Spaces-355
# 3. Azure Event Grid system topics + subscriptions on rg-cmdemo1-nonprod & rg-cmdemo1-prod (if provider registered)

: "${TWIN_URL:?TWIN_URL is required (e.g. https://ca-cmdemo1-twin....azurecontainerapps.io)}"
: "${GH_PAT:?GH_PAT is required}"
: "${OCTOPUS_URL:?OCTOPUS_URL is required}"
: "${OCTOPUS_API_KEY:?OCTOPUS_API_KEY is required}"
: "${WEBHOOK_SECRET:?WEBHOOK_SECRET is required}"

TWIN_URL="${TWIN_URL%/}"
GITHUB_ORG="clearmeasure-aisf-sample-apps"
OCTO_BASE="${OCTOPUS_URL%/}"
OCTO_SPACE="Spaces-355"
SUB_NAME="cmdemo1-digitaltwin"

echo "=== 1. Registering GitHub webhooks on all active cmdemo1-* repos -> ${TWIN_URL}/hooks/github ==="
SEED_REPOS="cmdemo1-system cmdemo1-workorders cmdemo1-dashboard cmdemo1-aisf cmdemo1-fworker cmdemo1-app2-ui cmdemo1-scorecard cmdemo1-scorecard-site cmdemo1-video cmdemo1-digitaltwin"
SEARCH_JSON=$(curl -fsSL \
  -H "Authorization: Bearer ${GH_PAT}" \
  -H "Accept: application/vnd.github+json" \
  "https://api.github.com/search/repositories?q=org:${GITHUB_ORG}+cmdemo1-+in:name&per_page=100" || echo '{"items":[]}')

FOUND_REPOS=$(echo "$SEARCH_JSON" | jq -r '.items[]? | select(.name | startswith("cmdemo1-")) | .name')
REPO_LIST=$(printf '%s\n%s\n' "$SEED_REPOS" "$FOUND_REPOS" | grep -E '^cmdemo1-' | sort -u)

for repo in $REPO_LIST; do
  REPO_INFO=$(curl -fsSL \
    -H "Authorization: Bearer ${GH_PAT}" \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${GITHUB_ORG}/${repo}" 2>/dev/null || echo '{}')
  IS_ARCHIVED=$(echo "$REPO_INFO" | jq -r '.archived // false')
  if [ "$IS_ARCHIVED" = "true" ]; then
    echo "  SKIP archived repo ${repo}"
    continue
  fi

  HOOKS_JSON=$(curl -fsSL \
    -H "Authorization: Bearer ${GH_PAT}" \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks")

  EXISTING_ID=$(echo "$HOOKS_JSON" | jq -r --arg target "${TWIN_URL}/hooks/github" '
    [.[] | select((.config.url // "") == $target or ((.config.url // "") | test("ca-cmdemo1-twin.*/hooks/github$")))][0].id // empty
  ')

  PAYLOAD=$(jq -n --arg url "${TWIN_URL}/hooks/github" --arg secret "${WEBHOOK_SECRET}" '{
    name: "web",
    active: true,
    events: ["*"],
    config: {
      url: $url,
      content_type: "json",
      secret: $secret,
      insecure_ssl: "0"
    }
  }')

  if [ -n "$EXISTING_ID" ]; then
    curl -fsSL -X PATCH \
      -H "Authorization: Bearer ${GH_PAT}" \
      -H "Accept: application/vnd.github+json" \
      -d "$PAYLOAD" \
      "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${EXISTING_ID}" >/dev/null
    HOOK_ID="$EXISTING_ID"
    echo "  UPDATED github hook ${repo} (id=${HOOK_ID})"
  else
    CREATED=$(curl -fsSL -X POST \
      -H "Authorization: Bearer ${GH_PAT}" \
      -H "Accept: application/vnd.github+json" \
      -d "$PAYLOAD" \
      "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks")
    HOOK_ID=$(echo "$CREATED" | jq -r '.id')
    echo "  CREATED github hook ${repo} (id=${HOOK_ID})"
  fi

  # Ping hook to confirm live 2xx delivery
  curl -fsSL -X POST \
    -H "Authorization: Bearer ${GH_PAT}" \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${HOOK_ID}/pings" >/dev/null || true
done

sleep 2
echo "=== Verifying recent GitHub webhook deliveries ==="
for repo in $REPO_LIST; do
  HOOKS_JSON=$(curl -fsSL \
    -H "Authorization: Bearer ${GH_PAT}" \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks" 2>/dev/null || echo '[]')
  HOOK_ID=$(echo "$HOOKS_JSON" | jq -r --arg target "${TWIN_URL}/hooks/github" '[.[] | select((.config.url // "") == $target)][0].id // empty')
  if [ -n "$HOOK_ID" ]; then
    DEL=$(curl -fsSL \
      -H "Authorization: Bearer ${GH_PAT}" \
      -H "Accept: application/vnd.github+json" \
      "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${HOOK_ID}/deliveries?per_page=1" | jq -r '.[0] | "\(.event) HTTP \(.status_code) (\(.status))"' || echo "pending")
    echo "  ${repo} (hook=${HOOK_ID}): ${DEL}"
  fi
done

echo "=== 2. Registering Octopus Subscription '${SUB_NAME}' in ${OCTO_SPACE} -> ${TWIN_URL}/hooks/octopus ==="
CATEGORIES_JSON=$(curl -fsSL -H "X-Octopus-ApiKey: ${OCTOPUS_API_KEY}" "${OCTO_BASE}/api/events/categories")
CATEGORIES_ARRAY=$(echo "$CATEGORIES_JSON" | jq '[.[].Id | select((startswith("Login") or startswith("License") or startswith("UsageDeclaration") or startswith("ApiKey") or startswith("Certificate") or startswith("SigningKey") or startswith("McpOAuth")) | not)]')

SUBS_JSON=$(curl -fsSL -H "X-Octopus-ApiKey: ${OCTOPUS_API_KEY}" "${OCTO_BASE}/api/${OCTO_SPACE}/subscriptions?take=100")
EXISTING_SUB_ID=$(echo "$SUBS_JSON" | jq -r --arg name "$SUB_NAME" '[.Items[] | select(.Name == $name)][0].Id // empty')

SUB_PAYLOAD=$(jq -n \
  --arg id "$EXISTING_SUB_ID" \
  --arg name "$SUB_NAME" \
  --arg space "$OCTO_SPACE" \
  --arg uri "${TWIN_URL}/hooks/octopus" \
  --arg secret "${WEBHOOK_SECRET}" \
  --argjson cats "$CATEGORIES_ARRAY" '
  {
    Name: $name,
    Type: "Event",
    IsDisabled: false,
    SpaceId: $space,
    EventNotificationSubscription: {
      Filter: {
        Users: [],
        Projects: [],
        ProjectGroups: [],
        Environments: [],
        EventGroups: [],
        EventCategories: $cats,
        EventAgents: [],
        Tenants: [],
        Tags: [],
        DocumentTypes: []
      },
      EmailTeams: [],
      EmailFrequencyPeriod: "01:00:00",
      EmailPriority: "Normal",
      EmailShowDatesInTimeZoneId: "UTC",
      WebhookURI: $uri,
      WebhookTeams: [],
      WebhookTimeout: "00:00:10",
      WebhookHeaderKey: "X-Webhook-Secret",
      WebhookHeaderValue: $secret
    }
  } + (if $id != "" then { Id: $id } else {} end)')

if [ -n "$EXISTING_SUB_ID" ]; then
  curl -fsSL -X PUT \
    -H "X-Octopus-ApiKey: ${OCTOPUS_API_KEY}" \
    -H "Content-Type: application/json" \
    -d "$SUB_PAYLOAD" \
    "${OCTO_BASE}/api/${OCTO_SPACE}/subscriptions/${EXISTING_SUB_ID}" >/dev/null
  echo "  UPDATED Octopus subscription ${EXISTING_SUB_ID} ($(echo "$CATEGORIES_ARRAY" | jq 'length') categories)"
else
  CREATED_SUB=$(curl -fsSL -X POST \
    -H "X-Octopus-ApiKey: ${OCTOPUS_API_KEY}" \
    -H "Content-Type: application/json" \
    -d "$SUB_PAYLOAD" \
    "${OCTO_BASE}/api/${OCTO_SPACE}/subscriptions")
  echo "  CREATED Octopus subscription $(echo "$CREATED_SUB" | jq -r '.Id')"
fi

echo "=== 3. Checking Azure Event Grid provider & system topics ==="
EG_STATE=$(az provider show --namespace Microsoft.EventGrid --query registrationState -o tsv 2>/dev/null || echo "Unknown")
if [ "$EG_STATE" != "Registered" ]; then
  echo "  Microsoft.EventGrid is '${EG_STATE}'; attempting registration..."
  set +e
  az provider register --namespace Microsoft.EventGrid --wait >/dev/null 2>&1
  REG_RC=$?
  set -e
  EG_STATE=$(az provider show --namespace Microsoft.EventGrid --query registrationState -o tsv 2>/dev/null || echo "Unknown")
  if [ "$REG_RC" -ne 0 ] || [ "$EG_STATE" != "Registered" ]; then
    echo "  NOTICE: Microsoft.EventGrid remains '${EG_STATE}' (RG-scoped Contributor cannot register subscription resource providers)."
    echo "  To enable Event Grid push delivery in addition to Resource Graph/ARM pollers, a subscription Owner should run:"
    echo "    az provider register --namespace Microsoft.EventGrid"
    echo "  and re-run this workflow."
    exit 0
  fi
fi

ENCODED_TOKEN=$(python3 -c 'import os,urllib.parse; print(urllib.parse.quote(os.environ["WEBHOOK_SECRET"], safe=""))')
EG_URL="${TWIN_URL}/hooks/azure?token=${ENCODED_TOKEN}"
for rg in rg-cmdemo1-nonprod rg-cmdemo1-prod; do
  echo "  Deploying Event Grid system topic & subscription in ${rg}..."
  az deployment group create \
    --resource-group "$rg" \
    --name "deploy-twin-eventgrid" \
    --template-file infra/eventgrid.bicep \
    --parameters webhookEndpointUrl="$EG_URL" \
    --output none
  echo "  ACTIVE Event Grid subscription in ${rg}"
done
