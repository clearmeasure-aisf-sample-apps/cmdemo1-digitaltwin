set -euo pipefail
PKG=$(get_octopusvariable "Octopus.Action.Package[twin].ExtractedPath")
export TWIN_URL=$(get_octopusvariable "Octopus.Action[Deploy twin].Output.TwinUrl")
export TWIN_API_KEY=$(get_octopusvariable "Twin.ApiKey")
export EXPECTED_VERSION=$(get_octopusvariable "Octopus.Release.Number")
bash "$PKG/deploy/scripts/verify-twin.sh"
