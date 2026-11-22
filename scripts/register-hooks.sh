#!/usr/bin/env bash
set -euo pipefail

# Idempotently register:
# 1. Octopus Deploy Subscription in Spaces-355 (no GitHub quota cost)
# 2. Azure Event Grid system topics (managed in code; idempotent, refuses to recreate); disable with ENABLE_EVENT_GRID_DEPLOY=false
# 3. GitHub repo webhooks across all active (non-archived) observed repos (quota-guarded!)

: "${TWIN_URL:?TWIN_URL is required (e.g. https://ca-cmdemo1-twin....azurecontainerapps.io)}"
: "${GH_PAT:?GH_PAT is required}"
: "${OCTOPUS_URL:?OCTOPUS_URL is required}"
: "${OCTOPUS_API_KEY:?OCTOPUS_API_KEY is required}"
: "${WEBHOOK_SECRET:?WEBHOOK_SECRET is required}"

TWIN_URL="${TWIN_URL%/}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Another platform overrides these (docs/setup/README.md); the defaults are cmdemo1's.
SLUG="${TWIN_SYSTEM_SLUG:-cmdemo1}"
GITHUB_ORG="${TWIN_GITHUB_ORG:-clearmeasure-aisf-sample-apps}"
OCTO_BASE="${OCTOPUS_URL%/}"
OCTO_SPACE="${TWIN_OCTOPUS_SPACE:-Spaces-355}"
SUB_NAME="${TWIN_NAME:-${SLUG}-digitaltwin}"
RESOURCE_GROUPS="${TWIN_RESOURCE_GROUPS:-rg-${SLUG}-nonprod rg-${SLUG}-prod}"

echo "=== 1. Registering Octopus Subscription '${SUB_NAME}' in ${OCTO_SPACE} -> ${TWIN_URL}/hooks/octopus ==="
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

echo "=== 2. Azure Event Grid system topics (ENABLE_EVENT_GRID_DEPLOY=${ENABLE_EVENT_GRID_DEPLOY:-false}) ==="
EG_STATE=$(az provider show --namespace Microsoft.EventGrid --query registrationState -o tsv 2>/dev/null || echo "Unknown")
echo "  Microsoft.EventGrid provider: ${EG_STATE}"
for rg in $RESOURCE_GROUPS; do
  TOPICS=$(az eventgrid system-topic list -g "$rg" --query "[].name" -o tsv 2>/dev/null | tr '\n' ' ' || true)
  echo "  ${rg}: system topics [${TOPICS:-none}]"
done

if [ "${ENABLE_EVENT_GRID_DEPLOY:-false}" = "true" ] && [ "$EG_STATE" = "Registered" ]; then
  ENCODED_TOKEN=$(python3 -c 'import os,urllib.parse; print(urllib.parse.quote(os.environ["WEBHOOK_SECRET"], safe=""))')
  EG_URL="${TWIN_URL}/hooks/azure?token=${ENCODED_TOKEN}"
  for rg in $RESOURCE_GROUPS; do
    TOPIC="eg-twin-${rg}"
    TOPIC_ID=$(az eventgrid system-topic show -g "$rg" -n "$TOPIC" --query id -o tsv 2>/dev/null || echo "")
    # metricResourceId is assigned when the topic is created, so it changes if the topic is ever recreated.
    BEFORE=""
    if [ -n "$TOPIC_ID" ]; then
      BEFORE=$(az eventgrid system-topic show -g "$rg" -n "$TOPIC" --query metricResourceId -o tsv)
    fi
    echo "  ${TOPIC}: before deploy metricResourceId=${BEFORE:-<absent>}"

    CHANGES=$(az deployment group what-if -g "$rg" --name "deploy-twin-eventgrid" \
      --template-file "$ROOT/infra/eventgrid.bicep" --parameters webhookEndpointUrl="$EG_URL" systemSlug="$SLUG" \
      --no-pretty-print --query "changes[].[changeType, resourceId]" -o tsv)
    echo "$CHANGES" | sed -E 's#/subscriptions/[^/]+#/subscriptions/<sub>#; s/^/    what-if: /'
    if [ -n "$TOPIC_ID" ] && echo "$CHANGES" | grep -qE '^(Create|Delete)'; then
      echo "::error::what-if for ${rg} would recreate Event Grid resources; refusing to deploy"
      exit 1
    fi

    az deployment group create -g "$rg" --name "deploy-twin-eventgrid" \
      --template-file "$ROOT/infra/eventgrid.bicep" --parameters webhookEndpointUrl="$EG_URL" systemSlug="$SLUG" --output none

    AFTER_ID=$(az eventgrid system-topic show -g "$rg" -n "$TOPIC" --query id -o tsv)
    AFTER=$(az eventgrid system-topic show -g "$rg" -n "$TOPIC" --query metricResourceId -o tsv)
    if [ -n "$TOPIC_ID" ] && { [ "$AFTER_ID" != "$TOPIC_ID" ] || [ "$AFTER" != "$BEFORE" ]; }; then
      echo "::error::${TOPIC} was recreated (metricResourceId ${BEFORE} -> ${AFTER})"
      exit 1
    fi
    echo "  ${TOPIC}: after deploy metricResourceId=${AFTER} (same topic, not recreated)"

    az eventgrid system-topic event-subscription show -g "$rg" --system-topic-name "$TOPIC" -n twin-observer \
      --query "{subscription:name, state:provisioningState, endpoint:destination.endpointBaseUrl}" -o tsv | sed 's/^/    /'
    az monitor metrics list --resource "$AFTER_ID" --metric DeliverySuccessCount DeliveryAttemptFailCount DroppedEventCount \
      --interval PT1H --aggregation Total --start-time "$(date -u -d '-24 hours' +%Y-%m-%dT%H:%M:%SZ)" -o json 2>/dev/null \
      | jq -r '.value[] | "    delivery last 24h \(.name.value)=\([.timeseries[0].data[]?.total // 0] | add // 0)"' || echo "    delivery metrics unavailable"
  done
else
  echo "  Event Grid infra not deployed by this run (ENABLE_EVENT_GRID_DEPLOY=${ENABLE_EVENT_GRID_DEPLOY:-false})."
fi

echo "=== 3. Checking GitHub REST quota before registering repo webhooks ==="
RATE_JSON=$(curl -fsSL -H "Authorization: Bearer ${GH_PAT}" "https://api.github.com/rate_limit" 2>/dev/null || echo '{}')
CORE_REM=$(echo "$RATE_JSON" | jq -r '.resources.core.remaining // 0')
CORE_RESET=$(echo "$RATE_JSON" | jq -r '.resources.core.reset // 0')
echo "  GitHub core remaining: ${CORE_REM} (reset epoch=${CORE_RESET})"

if [ "${CORE_REM}" -lt 200 ]; then
  echo "  NOTICE: GitHub core remaining (${CORE_REM}) < 200; skipping webhook registration calls until quota resets."
  exit 0
fi

echo "=== 4. Registering GitHub webhooks on all active ${SLUG} repos -> ${TWIN_URL}/hooks/github ==="
SEED_REPOS="${TWIN_REPOSITORIES:-cmdemo1-system cmdemo1-workorders cmdemo1-dashboard cmdemo1-aisf cmdemo1-fworker cmdemo1-app2-ui cmdemo1-scorecard cmdemo1-scorecard-site cmdemo1-video cmdemo1-digitaltwin}"
ARCHIVED_SKIP="${TWIN_SKIP_REPOSITORIES:-cmdemo1-app2-ui}"

for repo in $SEED_REPOS; do
  if echo " $ARCHIVED_SKIP " | grep -q " $repo "; then
    echo "  SKIP archived repo ${repo}"
    continue
  fi

  HOOKS_JSON=$(curl -fsSL \
    -H "Authorization: Bearer ${GH_PAT}" \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks")

  EXISTING_ID=$(echo "$HOOKS_JSON" | jq -r --arg target "${TWIN_URL}/hooks/github" --arg slug "$SLUG" '
    [.[] | select((.config.url // "") == $target or ((.config.url // "") | test("ca-" + $slug + "-twin.*/hooks/github$")))][0].id // empty
  ')
  ALREADY_OK=$(echo "$HOOKS_JSON" | jq -r --arg target "${TWIN_URL}/hooks/github" '
    [.[] | select((.config.url // "") == $target and .active == true)][0].id // empty
  ')

  if [ -n "$ALREADY_OK" ]; then
    DEL_JSON=$(curl -fsSL \
      -H "Authorization: Bearer ${GH_PAT}" \
      -H "Accept: application/vnd.github+json" \
      "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${ALREADY_OK}/deliveries?per_page=1" || echo '[]')
    if [ "$(echo "$DEL_JSON" | jq 'length')" -eq 0 ]; then
      curl -fsSL -X POST \
        -H "Authorization: Bearer ${GH_PAT}" \
        -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${ALREADY_OK}/pings" >/dev/null || true
      sleep 1
      DEL_JSON=$(curl -fsSL \
        -H "Authorization: Bearer ${GH_PAT}" \
        -H "Accept: application/vnd.github+json" \
        "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${ALREADY_OK}/deliveries?per_page=1" || echo '[]')
    fi
    DEL=$(echo "$DEL_JSON" | jq -r '.[0] // empty | "\(.event) HTTP \(.status_code) (\(.status))"' || echo "pending")
    echo "  OK github hook ${repo} (id=${ALREADY_OK}, last=${DEL:-pending})"
    continue
  fi

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

  curl -fsSL -X POST \
    -H "Authorization: Bearer ${GH_PAT}" \
    -H "Accept: application/vnd.github+json" \
    "https://api.github.com/repos/${GITHUB_ORG}/${repo}/hooks/${HOOK_ID}/pings" >/dev/null || true
done
