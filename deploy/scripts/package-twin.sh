#!/usr/bin/env bash
# Builds the Octopus package for one release candidate: cmdemo1-digitaltwin.<VERSION>.zip
# (compiled ARM template, Bicep sources, deploy/verify scripts, hook registration, release.json).
set -euo pipefail
: "${VERSION:?VERSION is required}"
: "${IMAGE:?IMAGE is required}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PACKAGE_ID="${PACKAGE_ID:-cmdemo1-digitaltwin}"
OUT="${OUT_DIR:-$ROOT/out}"
STAGE="$OUT/package"
rm -rf "$STAGE" && mkdir -p "$STAGE/infra" "$STAGE/deploy/scripts" "$STAGE/scripts" "$STAGE/docs/platforms"
cp "$ROOT"/infra/*.bicep "$STAGE/infra/"
az bicep build --file "$ROOT/infra/main.bicep" --outfile "$STAGE/infra/main.json"
cp "$ROOT"/deploy/scripts/*.sh "$STAGE/deploy/scripts/"
cp "$ROOT/scripts/register-hooks.sh" "$STAGE/scripts/"
cp "$ROOT"/docs/platforms/*.platform.json "$STAGE/docs/platforms/"
chmod +x "$STAGE"/deploy/scripts/*.sh "$STAGE"/scripts/*.sh
jq -n --arg id "$PACKAGE_ID" --arg v "$VERSION" --arg img "$IMAGE" --arg sha "${COMMIT_SHA:-$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)}" \
  --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '{packageId: $id, version: $v, image: $img, commit: $sha, builtAt: $at}' > "$STAGE/release.json"
rm -f "$OUT/${PACKAGE_ID}.${VERSION}.zip"
(cd "$STAGE" && zip -qr "$OUT/${PACKAGE_ID}.${VERSION}.zip" .)
echo "$OUT/${PACKAGE_ID}.${VERSION}.zip"
