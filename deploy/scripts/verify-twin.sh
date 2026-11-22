#!/usr/bin/env bash
# Post-deploy health gate shared by GitHub Actions and the Octopus process:
# serving the expected version, intake healthy with no dead letters, storage within budget, MCP answering.
set -euo pipefail
: "${TWIN_URL:?TWIN_URL is required}"
: "${TWIN_API_KEY:?TWIN_API_KEY is required}"
TWIN_URL="${TWIN_URL%/}"
K="X-Twin-Key: ${TWIN_API_KEY}"

HEALTH=$(curl -fsS -m 30 --retry 3 --retry-all-errors --retry-delay 10 "${TWIN_URL}/health")
echo "$HEALTH" | jq -c '{status, version}'
echo "$HEALTH" | jq -e '.status == "Healthy"' >/dev/null
if [ -n "${EXPECTED_VERSION:-}" ]; then
  echo "$HEALTH" | jq -e --arg v "$EXPECTED_VERSION" '.version == $v' >/dev/null || { echo "Serving $(echo "$HEALTH" | jq -r .version), expected ${EXPECTED_VERSION}" >&2; exit 1; }
fi

INTAKE=$(curl -fsS -m 30 "${TWIN_URL}/api/intake/status")
echo "$INTAKE" | jq -c '{transport, depth, deadLetterCount}'
echo "$INTAKE" | jq -e '.deadLetterCount == 0' >/dev/null || { echo "Dead letters present; replay or inspect /api/admin/deadletters" >&2; exit 1; }

STORAGE=$(curl -fsS -m 60 -H "$K" "${TWIN_URL}/api/admin/storage")
echo "$STORAGE" | jq -c '{usedMb: .database.usedMb, maxMb: .database.maxMb, archiveEnabled: .archive.enabled, archiveDays: .archive.days}'
echo "$STORAGE" | jq -e '.database.usedMb < 1500' >/dev/null || { echo "Database above 1500 MB of the Basic tier's 2 GB" >&2; exit 1; }

MCP=$(curl -fsS -m 30 -X POST "${TWIN_URL}/mcp" -H "$K" -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"verify-twin","version":"1.0"}}}')
echo "$MCP" | grep -q '"serverInfo"' || { echo "MCP initialize did not answer" >&2; exit 1; }
echo "Twin verified at ${TWIN_URL}"
