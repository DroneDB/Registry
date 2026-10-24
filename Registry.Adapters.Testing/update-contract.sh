#!/usr/bin/env bash
# Vendors the libddb contract fixtures (XI-09) from a DroneDB checkout at one commit and rewrites
# Contract/SOURCE (the commit and the sha256 of every vendored file).
#
# Usage: Registry.Adapters.Testing/update-contract.sh <path to a DroneDB clone> <commit>
#
# The files come from `git archive <commit>`, so they are the committed bytes of that commit,
# whatever the checkout or its line-ending settings. ContractSourceTests fails when a vendored
# file no longer matches SOURCE, so a fixture changes only through this script.
set -euo pipefail
export LC_ALL=C

if [[ $# -ne 2 ]]; then
    echo "usage: $0 <path to a DroneDB clone> <commit>" >&2
    exit 2
fi

ddb_repo="$1"
commit="$(git -C "$ddb_repo" rev-parse --verify "$2^{commit}")"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
contract="$here/Contract"
source_dir="tests/contract"
# What Registry consumes: the schemas, the golden fixtures, the golden vectors and their README
vendored=("$source_dir/README.md" "$source_dir/schemas" "$source_dir/fixtures" "$source_dir/vectors")

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

git -C "$ddb_repo" archive "$commit" "${vendored[@]}" | tar -x -C "$tmp"

rm -rf "$contract/README.md" "$contract/schemas" "$contract/fixtures" "$contract/vectors"
mkdir -p "$contract"
cp -R "$tmp/$source_dir/." "$contract/"

{
    echo "# libddb contract fixtures vendored from DroneDB (XI-09). Do not edit the vendored files:"
    echo "# run Registry.Adapters.Testing/update-contract.sh <DroneDB clone> <commit> instead."
    echo "repository: https://github.com/DroneDB/DroneDB"
    echo "commit: $commit"
    echo "path: $source_dir"
    echo "# sha256 and path relative to Contract/ (sha256sum format), one line per vendored file"
    (cd "$contract" && find . -type f ! -name SOURCE ! -name .gitattributes | sed 's|^\./||' | sort |
        xargs -d '\n' sha256sum)
} >"$contract/SOURCE"

echo "Vendored $(grep -c '^[0-9a-f]\{64\}  ' "$contract/SOURCE") files from DroneDB $commit."
echo "Review the diff, run the Registry.Web.UnitTest contract tests and list the change in Registry.Adapters.Testing/README.md."
