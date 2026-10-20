#!/usr/bin/env bash
# Warning ratchet (RQ-03, decision D7): the build may not add a compiler or analyzer warning.
#
# The baseline (build/warnings-baseline.txt) has one line per file and warning code:
#     <path from the repository root> <code> <count>
# <count> is the number of distinct locations (line, column) of that code in that file.
# Line numbers are not stored, so an edit that only moves code does not change the baseline,
# while a new warning in a file raises its count and fails the check.
#
# Not in the baseline: NuGet audit warnings (NU1900-NU1904). They depend on the advisory
# database on the day of the build, not on the change. The build log still shows them.
#
# Usage:
#   scripts/check-warnings.sh                 build with --no-incremental, then compare
#   scripts/check-warnings.sh <build-log>     compare the warnings of an existing full build log
#   scripts/check-warnings.sh --update [log]  write the current warnings to the baseline
#
# Exit codes: 0 no new warning, 1 new warnings, 2 build failed.
set -euo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASELINE="$ROOT/build/warnings-baseline.txt"

update=0
if [[ "${1:-}" == "--update" ]]; then
    update=1
    shift
fi
log="${1:-}"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [[ -z "$log" ]]; then
    log="$tmp/build.log"
    echo "Building Registry.sln (Release, --no-incremental)..."
    if ! dotnet build "$ROOT/Registry.sln" -c Release --no-incremental --verbosity minimal -tl:off -nologo >"$log" 2>&1; then
        tail -n 50 "$log" >&2
        echo "error: the build failed." >&2
        exit 2
    fi
fi

# Prints the unique warnings of a build log as "<path> <code> <count>", sorted.
extract_warnings() {
    grep -E ': warning [A-Za-z]+[0-9]+:' "$1" |
        sed -E 's/ \[[^]]*\]$//' |
        sed -E -n \
            -e 's/^[[:space:]]*(.*)\(([0-9]+(,[0-9]+)*)\): warning ([A-Za-z]+[0-9]+):.*/\1|\2|\4/p' \
            -e 't' \
            -e 's/^[[:space:]]*(.*[^[:space:]])[[:space:]]*: warning ([A-Za-z]+[0-9]+):.*/\1||\2/p' |
        awk -F'|' -v root="$ROOT/" '
            {
                path = $1
                gsub(/\\/, "/", path)
                if (index(path, root) == 1) path = substr(path, length(root) + 1)
                # Files outside the repository (SDK targets): keep the file name only,
                # so the key does not depend on where the SDK is installed.
                else if (path ~ /^(\/|[A-Za-z]:\/)/) { n = split(path, parts, "/"); path = parts[n] }
                if ($3 ~ /^NU190[0-4]$/) next
                print path "|" $2 "|" $3
            }' |
        sort -u |
        awk -F'|' '{ count[$1 " " $3]++ } END { for (k in count) print k, count[k] }' |
        sort
}

extract_warnings "$log" >"$tmp/current.txt"
total="$(awk '{ s += $NF } END { print s + 0 }' "$tmp/current.txt")"

if [[ $update -eq 1 ]]; then
    {
        echo "# Warning baseline for scripts/check-warnings.sh. Do not edit by hand:"
        echo "# run scripts/check-warnings.sh --update after removing warnings."
        echo "# Format: <path> <code> <number of distinct locations>"
        cat "$tmp/current.txt"
    } >"$BASELINE"
    echo "Baseline updated: $(wc -l <"$tmp/current.txt") entries, $total warnings."
    exit 0
fi

grep -Ev '^[[:space:]]*(#|$)' "$BASELINE" | sort >"$tmp/baseline.txt" || true

# Joins baseline and current on "<path> <code>"; prints "<key> <baseline count> <current count>".
awk '
    { key = $1; for (i = 2; i < NF; i++) key = key " " $i }
    FNR == NR { base[key] = $NF; next }
    { cur[key] = $NF }
    END {
        for (k in cur)  print k, (k in base ? base[k] : 0), cur[k]
        for (k in base) if (!(k in cur)) print k, base[k], 0
    }' "$tmp/baseline.txt" "$tmp/current.txt" | sort >"$tmp/joined.txt"

awk '$NF > $(NF-1)' "$tmp/joined.txt" >"$tmp/added.txt"
awk '$NF < $(NF-1)' "$tmp/joined.txt" >"$tmp/removed.txt"

if [[ -s "$tmp/removed.txt" ]]; then
    echo "Fewer warnings than the baseline (<path> <code> <baseline> -> <now>):"
    awk '{ printf "  %s %s -> %s\n", substr($0, 1, length($0) - length($(NF-1)) - length($NF) - 2), $(NF-1), $NF }' "$tmp/removed.txt"
    echo "Run scripts/check-warnings.sh --update and commit build/warnings-baseline.txt, so the ratchet keeps the gain."
    if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
        echo "::warning title=Warning baseline::Warnings were removed. Run scripts/check-warnings.sh --update and commit build/warnings-baseline.txt."
    fi
fi

if [[ -s "$tmp/added.txt" ]]; then
    echo "error: new warnings (<path> <code> <baseline> -> <now>):" >&2
    awk '{ printf "  %s %s -> %s\n", substr($0, 1, length($0) - length($(NF-1)) - length($NF) - 2), $(NF-1), $NF }' "$tmp/added.txt" >&2
    echo "Fix them. The locations are in the build output (dotnet build -c Release --no-incremental)." >&2
    exit 1
fi

echo "No new warnings: $total warnings in $(wc -l <"$tmp/current.txt") file/code entries."
