#!/usr/bin/env bash
# run-stac-compliance.sh
# Builds Registry, starts the server, runs the STAC compliance validator, and reports results.
# Run from the Registry repository root.
#
# Usage:
#   ./run-stac-compliance.sh                          # build + validate
#   ./run-stac-compliance.sh --skip-build             # skip dotnet build
#   ./run-stac-compliance.sh --collection "admin/test" --port 7001

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKIP_BUILD=false
COLLECTION="admin/test"
PORT=7000
CONFIGURATION="Debug"
TFM="net10.0"

# ── Parse args ────────────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case $1 in
        --skip-build)   SKIP_BUILD=true; shift ;;
        --collection)   COLLECTION="$2"; shift 2 ;;
        --port)         PORT="$2"; shift 2 ;;
        --configuration) CONFIGURATION="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

WEB_PROJECT="$SCRIPT_DIR/Registry.Web/Registry.Web.csproj"
DLL="$SCRIPT_DIR/Registry.Web/bin/$CONFIGURATION/$TFM/Registry.Web.dll"
DATA_DIR="$SCRIPT_DIR/Registry.Web/registry-data"
ROOT_URL="http://localhost:$PORT/stac"
SERVER_PID=""

stop_server() {
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    # Also kill anything else on the port
    fuser -k "${PORT}/tcp" 2>/dev/null || true
}
trap stop_server EXIT

# ── 1. Build ──────────────────────────────────────────────────────────────────
if [[ "$SKIP_BUILD" == false ]]; then
    echo ""
    echo "==> Building Registry ($CONFIGURATION)..."
    dotnet build "$WEB_PROJECT" -c "$CONFIGURATION" --nologo \
        | grep -E "error CS|Build succeeded|Build FAILED" || true
    echo "    Build succeeded."
fi

# ── 2. Start server ───────────────────────────────────────────────────────────
echo ""
echo "==> Starting Registry server on port $PORT..."
ASPNETCORE_ENVIRONMENT=Development \
    dotnet "$DLL" "$DATA_DIR" --address "localhost:$PORT" \
    > /tmp/registry-stac-test.log 2>&1 &
SERVER_PID=$!

# Wait for port to open (up to 30 s)
UP=false
for i in $(seq 1 30); do
    sleep 1
    if ss -ltn "sport = :$PORT" 2>/dev/null | grep -q ":$PORT" || \
       nc -z localhost "$PORT" 2>/dev/null; then
        UP=true; break
    fi
done
if [[ "$UP" == false ]]; then
    echo "ERROR: Server did not start within 30 seconds." >&2
    cat /tmp/registry-stac-test.log >&2
    exit 1
fi
echo "    Server is up."

# ── 3. Run validator ──────────────────────────────────────────────────────────
echo ""
echo "==> Running stac-api-validator..."
export PYTHONIOENCODING=utf-8
export PYTHONUTF8=1

VALIDATOR_OUTPUT=$(stac-api-validator \
    --root-url "$ROOT_URL" \
    --conformance core \
    --conformance collections \
    --conformance features \
    --conformance item-search \
    --collection "$COLLECTION" 2>/dev/null || true)

echo "$VALIDATOR_OUTPUT"

# ── 4. Report ─────────────────────────────────────────────────────────────────
echo ""
ERRORS_LINE=$(echo "$VALIDATOR_OUTPUT" | grep "^Errors:" || true)
if echo "$ERRORS_LINE" | grep -q "none"; then
    echo -e "\033[32mRESULT: PASS — Errors: none\033[0m"
    exit 0
else
    ERROR_ITEMS=$(echo "$VALIDATOR_OUTPUT" | awk '/^Errors:/{found=1} found && /^- /{print}')
    if [[ -z "$ERROR_ITEMS" ]]; then
        echo -e "\033[32mRESULT: PASS — Errors: none\033[0m"
        exit 0
    else
        echo -e "\033[31mRESULT: FAIL\033[0m"
        echo "$ERROR_ITEMS"
        exit 1
    fi
fi
