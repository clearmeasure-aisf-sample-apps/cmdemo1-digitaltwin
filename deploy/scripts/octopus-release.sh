#!/usr/bin/env bash
# Pushes one release candidate package to the space's built-in feed, creates the release, then follows it while the
# twin's lifecycle deploys it to each environment in turn. Fails as soon as one deployment does not succeed.
# Never deploys or promotes by itself: the lifecycle (automatic phases) does.
set -euo pipefail
: "${OCTOPUS_URL:?}" "${OCTOPUS_API_KEY:?}" "${VERSION:?}" "${PACKAGE_FILE:?}"
SPACE="${SPACE:-Spaces-355}"
PROJECT="${PROJECT:-cmdemo1-digitaltwin}"
ENVIRONMENTS="${ENVIRONMENTS:-tdd uat prod}"
B="${OCTOPUS_URL%/}"
H="X-Octopus-ApiKey: ${OCTOPUS_API_KEY}"
api() { curl -fsS -H "$H" "$@"; }

echo "== Package $(basename "$PACKAGE_FILE")"
api -X POST -F "file=@${PACKAGE_FILE}" "${B}/api/${SPACE}/packages/raw?overwriteMode=IgnoreIfExists" | jq -c '{PackageId, Version}'

PROJECT_ID=$(api "${B}/api/${SPACE}/projects/${PROJECT}" | jq -r .Id)
RELEASE_ID=$(curl -sS -H "$H" "${B}/api/${SPACE}/projects/${PROJECT_ID}/releases/${VERSION}" | jq -r '.Id // empty')
if [ -z "$RELEASE_ID" ]; then
  RELEASE_ID=$(jq -n --arg space "$SPACE" --arg project "$PROJECT" --arg v "$VERSION" \
      '{spaceIdOrName: $space, projectName: $project, releaseVersion: $v, packageVersion: $v}' \
    | api -X POST -H "Content-Type: application/json" -d @- "${B}/api/${SPACE}/releases/create/v1" | jq -r .ReleaseId)
fi
echo "Release ${VERSION}: ${RELEASE_ID} (${B}/app#/${SPACE}/projects/${PROJECT}/deployments/releases/${VERSION})"

ENVS_JSON=$(api "${B}/api/${SPACE}/environments/all")
SUMMARY="| Environment | Deployment | Started (UTC) | Finished (UTC) | Duration | State |\n| --- | --- | --- | --- | --- | --- |\n"
for ENV in $ENVIRONMENTS; do
  ENV_ID=$(echo "$ENVS_JSON" | jq -r --arg n "$ENV" '.[] | select(.Name == $n) | .Id')
  echo "== Waiting for ${ENV} (${ENV_ID})"
  TASK_ID=""
  for i in $(seq 1 60); do
    TASK_ID=$(api "${B}/api/${SPACE}/releases/${RELEASE_ID}/deployments?take=20" | jq -r --arg e "$ENV_ID" '[.Items[] | select(.EnvironmentId == $e)][0].TaskId // empty')
    [ -n "$TASK_ID" ] && break
    sleep 10
  done
  if [ -z "$TASK_ID" ]; then echo "No ${ENV} deployment was started for ${VERSION} within 10 minutes" >&2; exit 1; fi
  for i in $(seq 1 240); do
    TASK=$(api "${B}/api/tasks/${TASK_ID}")
    STATE=$(echo "$TASK" | jq -r .State)
    case "$STATE" in Success|Failed|TimedOut|Canceled) break ;; esac
    sleep 15
  done
  echo "$TASK" | jq -c '{env: "'"$ENV"'", TaskId: .Id, State, StartTime, CompletedTime, Duration}'
  SUMMARY+="| ${ENV} | ${TASK_ID} | $(echo "$TASK" | jq -r .StartTime) | $(echo "$TASK" | jq -r .CompletedTime) | $(echo "$TASK" | jq -r .Duration) | ${STATE} |\n"
  if [ "$STATE" != "Success" ]; then
    echo "${ENV} deployment of ${VERSION} ended ${STATE}; last log lines:" >&2
    api "${B}/api/tasks/${TASK_ID}/raw" | tail -n 60 >&2 || true
    exit 1
  fi
done

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  { echo "## Octopus release ${VERSION}"; echo; printf "%b" "$SUMMARY"; } >> "$GITHUB_STEP_SUMMARY"
fi
echo "Release ${VERSION} deployed to: ${ENVIRONMENTS}"
