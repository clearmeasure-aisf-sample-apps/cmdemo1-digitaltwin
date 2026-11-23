# Octopus bootstrap for the gate environments: the release candidate's image exists, a Bicep what-if deletes nothing,
# and the live twin is healthy. Nothing is deployed here; the rollout environment deploys.
set -euo pipefail
command -v jq >/dev/null || { curl -fsSL -o /tmp/jq https://github.com/jqlang/jq/releases/download/jq-1.7.1/jq-linux-amd64 && chmod +x /tmp/jq && export PATH="/tmp:$PATH"; }
PKG=$(get_octopusvariable "Octopus.Action.Package[twin].ExtractedPath")
export ARM_CLIENT_ID=$(get_octopusvariable "Azure.ClientId")
export ARM_CLIENT_SECRET=$(get_octopusvariable "Azure.ClientSecret")
export ARM_TENANT_ID=$(get_octopusvariable "Azure.TenantId")
export ARM_SUBSCRIPTION_ID=$(get_octopusvariable "Azure.SubscriptionId")
export OCTOPUS_URL=$(get_octopusvariable "Twin.OctopusUrl")
export OCTOPUS_API_KEY=$(get_octopusvariable "Twin.OctopusApiKey")
export GH_PAT=$(get_octopusvariable "Twin.GitHubPat")
export WEBHOOK_SECRET=$(get_octopusvariable "Twin.WebhookSecret")
export TWIN_API_KEY=$(get_octopusvariable "Twin.ApiKey")
export TWIN_RESOURCE_GROUP=$(get_octopusvariable "Twin.ResourceGroup")
export TWIN_APP_NAME=$(get_octopusvariable "Twin.AppName")
export ACR_NAME=$(get_octopusvariable "Twin.AcrName")
export IMAGE=$(get_octopusvariable "Twin.Image")
export VERSION=$(get_octopusvariable "Octopus.Release.Number")
export TWIN_SYSTEM_SLUG=$(get_octopusvariable "Twin.SystemSlug")
export TWIN_INTAKE_TRANSPORT=$(get_octopusvariable "Twin.IntakeTransport")
PLATFORM_FILE=$(get_octopusvariable "Twin.PlatformFile")
if [ -n "$PLATFORM_FILE" ]; then export TWIN_PLATFORM_FILE="$PKG/$PLATFORM_FILE"; fi
export TWIN_WHAT_IF=true
export TWIN_OUTPUT_FILE=$(mktemp)
bash "$PKG/deploy/scripts/azure-login.sh"
bash "$PKG/deploy/scripts/deploy-twin.sh"
set_octopusvariable "TwinUrl" "$(cat "$TWIN_OUTPUT_FILE")"
