# Health gate in every environment; the rollout environment must also serve this release.
set -euo pipefail
command -v jq >/dev/null || { curl -fsSL -o /tmp/jq https://github.com/jqlang/jq/releases/download/jq-1.7.1/jq-linux-amd64 && chmod +x /tmp/jq && export PATH="/tmp:$PATH"; }
PKG=$(get_octopusvariable "Octopus.Action.Package[twin].ExtractedPath")
export TWIN_URL=$(get_octopusvariable "Octopus.Action[Deploy twin].Output.TwinUrl")
if [ -z "$TWIN_URL" ]; then export TWIN_URL=$(get_octopusvariable "Octopus.Action[Preflight release candidate].Output.TwinUrl"); fi
export TWIN_API_KEY=$(get_octopusvariable "Twin.ApiKey")
if [ "$(get_octopusvariable "Octopus.Environment.Name")" = "$(get_octopusvariable "Twin.RolloutEnvironment")" ]; then
  export EXPECTED_VERSION=$(get_octopusvariable "Octopus.Release.Number")
fi
bash "$PKG/deploy/scripts/verify-twin.sh"
