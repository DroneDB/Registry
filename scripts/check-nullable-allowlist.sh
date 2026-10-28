#!/usr/bin/env bash
# Checks that the projects and files that opt out of nullable reference types are
# exactly the ones listed in build/nullable-allowlist.txt.
#
#   - Directory.Build.props must enable Nullable for every project.
#   - A csproj that sets <Nullable> to anything but `enable` must be listed by directory name.
#   - A .cs file with `#nullable disable` must be listed by path (EF Core migrations excepted).
#   - Every entry must still opt out, so the list can only shrink.
#
# Usage: scripts/check-nullable-allowlist.sh
set -euo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ALLOWLIST="$ROOT/build/nullable-allowlist.txt"
cd "$ROOT"

if ! grep -Eq '<Nullable>enable</Nullable>' Directory.Build.props; then
    echo "error: Directory.Build.props does not set <Nullable>enable</Nullable>." >&2
    exit 1
fi

# Tracked and new (not ignored) files, so bin/, obj/ and node_modules/ are skipped.
list_files() {
    git ls-files --cached --others --exclude-standard -- "$@"
}

actual="$(
    {
        # Projects: <Nullable> set to disable, warnings or annotations.
        list_files '*.csproj' | while read -r proj; do
            if grep -Eq '<Nullable>[[:space:]]*(disable|warnings|annotations)[[:space:]]*</Nullable>' "$proj"; then
                dirname "$proj"
            fi
        done
        # Files: `#nullable disable`, except EF Core migrations, which are generated with it.
        list_files '*.cs' | grep -v '/Migrations/' | grep -v '^Registry.Web/ClientApp/' |
            xargs -r grep -lE '^[[:space:]]*#nullable[[:space:]]+disable\b' || true
    } | sort -u
)"

# An empty list (only comments) is the goal, not an error.
expected="$({ grep -Ev '^[[:space:]]*(#|$)' "$ALLOWLIST" || true; } | sed -E 's/[[:space:]]+$//' | sort -u)"

unlisted="$(comm -23 <(printf '%s\n' "$actual" | sed '/^$/d') <(printf '%s\n' "$expected" | sed '/^$/d'))"
stale="$(comm -13 <(printf '%s\n' "$actual" | sed '/^$/d') <(printf '%s\n' "$expected" | sed '/^$/d'))"

status=0
if [[ -n "$unlisted" ]]; then
    echo "error: these opt out of nullable reference types but are not in build/nullable-allowlist.txt:" >&2
    sed 's/^/  /' <<<"$unlisted" >&2
    echo "Enable nullable for new code instead. The allow-list may only shrink." >&2
    status=1
fi
if [[ -n "$stale" ]]; then
    echo "error: these are in build/nullable-allowlist.txt but no longer opt out. Remove them from the list:" >&2
    sed 's/^/  /' <<<"$stale" >&2
    status=1
fi

if [[ $status -eq 0 ]]; then
    echo "Nullable allow-list OK: $(printf '%s\n' "$expected" | sed '/^$/d' | wc -l) entries."
fi
exit $status
