set -euo pipefail
PKG=$(get_octopusvariable "Octopus.Action.Package[twin].ExtractedPath")
export ARM_CLIENT_ID=$(get_octopusvariable "Azure.ClientId")
export ARM_CLIENT_SECRET=$(get_octopusvariable "Azure.ClientSecret")
export ARM_TENANT_ID=$(get_octopusvariable "Azure.TenantId")
export ARM_SUBSCRIPTION_ID=$(get_octopusvariable "Azure.SubscriptionId")
export TWIN_URL=$(get_octopusvariable "Octopus.Action[Deploy twin].Output.TwinUrl")
export OCTOPUS_URL=$(get_octopusvariable "Twin.OctopusUrl")
export OCTOPUS_API_KEY=$(get_octopusvariable "Twin.OctopusApiKey")
export GH_PAT=$(get_octopusvariable "Twin.GitHubPat")
export WEBHOOK_SECRET=$(get_octopusvariable "Twin.WebhookSecret")
export ENABLE_EVENT_GRID_DEPLOY=$(get_octopusvariable "Twin.EnableEventGrid")
export TWIN_SYSTEM_SLUG=$(get_octopusvariable "Twin.SystemSlug")
export TWIN_GITHUB_ORG=$(get_octopusvariable "Twin.GitHubOrg")
export TWIN_OCTOPUS_SPACE=$(get_octopusvariable "Twin.OctopusSpace")
bash "$PKG/deploy/scripts/azure-login.sh"
bash "$PKG/scripts/register-hooks.sh"
