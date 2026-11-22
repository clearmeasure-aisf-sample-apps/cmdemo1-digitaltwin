# Octopus bootstrap: map project variables to the environment deploy/scripts expects, then run the packaged script.
set -euo pipefail
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
export TWIN_OUTPUT_FILE=$(mktemp)
bash "$PKG/deploy/scripts/azure-login.sh"
bash "$PKG/deploy/scripts/deploy-twin.sh"
set_octopusvariable "TwinUrl" "$(cat "$TWIN_OUTPUT_FILE")"
