#!/usr/bin/env bash
# Seeds a Registry instance with the OGC test dataset and prints the resulting
# WMS / WFS / WMTS / WCS / OGC API / MVT endpoints so they can be loaded into
# QGIS for smoke-testing the OGC service stack.
#
# Usage:
#   ./qgis-test-setup.sh [--base-url URL] [--user USER] [--password PASS]
#                         [--org SLUG] [--ds SLUG] [--seed PATH]
#
# Requires: curl, jq

set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:7000}"
USERNAME="${USERNAME:-admin}"
PASSWORD="${PASSWORD:-_Rainbow1}"
ORG_SLUG="${ORG_SLUG:-qgis-test}"
DS_SLUG="${DS_SLUG:-ogc-fixture}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SEED_FOLDER="${SEED_FOLDER:-$SCRIPT_DIR/../../test_data/ogc-seed}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-url)  BASE_URL="$2";    shift 2 ;;
    --user)      USERNAME="$2";    shift 2 ;;
    --password)  PASSWORD="$2";    shift 2 ;;
    --org)       ORG_SLUG="$2";    shift 2 ;;
    --ds)        DS_SLUG="$2";     shift 2 ;;
    --seed)      SEED_FOLDER="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

command -v jq   >/dev/null || { echo "jq is required"   >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }

echo "==> Authenticating against $BASE_URL as $USERNAME ..."
TOKEN=$(curl -fsS -X POST "$BASE_URL/users/authenticate" \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"$USERNAME\",\"password\":\"$PASSWORD\"}" | jq -r .token)
[[ -n "$TOKEN" && "$TOKEN" != "null" ]] || { echo "Auth failed" >&2; exit 1; }

echo "==> Ensuring organization '$ORG_SLUG' ..."
curl -fsS -o /dev/null -X POST "$BASE_URL/orgs" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"slug\":\"$ORG_SLUG\",\"name\":\"QGIS Test Org\",\"isPublic\":true}" || true

echo "==> Recreating dataset '$DS_SLUG' ..."
curl -fsS -o /dev/null -X DELETE "$BASE_URL/orgs/$ORG_SLUG/ds/$DS_SLUG" \
  -H "Authorization: Bearer $TOKEN" || true
curl -fsS -o /dev/null -X POST "$BASE_URL/orgs/$ORG_SLUG/ds" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"slug\":\"$DS_SLUG\",\"name\":\"OGC Test Fixture\",\"isPublic\":true}"

if [[ ! -d "$SEED_FOLDER" ]]; then
  echo "Seed folder '$SEED_FOLDER' not found. Skipping upload step." >&2
else
  echo "==> Uploading seed files from $SEED_FOLDER ..."
  pushd "$SEED_FOLDER" >/dev/null
  find . -type f | while read -r f; do
    rel="${f#./}"
    echo "    + $rel"
    curl -fsS -o /dev/null -X POST "$BASE_URL/orgs/$ORG_SLUG/ds/$DS_SLUG/obj" \
      -H "Authorization: Bearer $TOKEN" \
      -F "path=$rel" -F "file=@$f" || echo "    (upload of $rel failed)" >&2
  done
  popd >/dev/null

  echo "==> Triggering build (thumbs + COG + MVT) ..."
  curl -fsS -o /dev/null -X POST "$BASE_URL/orgs/$ORG_SLUG/ds/$DS_SLUG/build" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}' \
    || echo "Build request failed" >&2
fi

ROOT="$BASE_URL/orgs/$ORG_SLUG/ds/$DS_SLUG"

cat <<EOF

====================================================================
  OGC endpoints (ready to paste into QGIS connections)
====================================================================
  WMS  GetCapabilities  : $ROOT/wms?service=WMS&request=GetCapabilities&version=1.3.0
  WFS  GetCapabilities  : $ROOT/wfs?service=WFS&request=GetCapabilities&version=2.0.0
  WMTS GetCapabilities  : $ROOT/wmts?service=WMTS&request=GetCapabilities&version=1.0.0
  WCS  GetCapabilities  : $ROOT/wcs?service=WCS&request=GetCapabilities&version=2.0.1
  OGC API – Features    : $ROOT/ogcapi/features
  OGC API – Tiles       : $ROOT/ogcapi/tiles
  MVT pyramid template  : $ROOT/mvt/{hash}/{z}/{x}/{y}.pbf
====================================================================

QGIS project template: scripts/qgis/ogc-test.qgz
EOF
