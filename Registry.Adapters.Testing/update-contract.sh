#!/usr/bin/env bash
# Vendors the libddb contract fixtures from a DroneDB checkout at one commit and rewrites
# Contract/SOURCE (the commit and the sha256 of every vendored file).
#
# Usage: Registry.Adapters.Testing/update-contract.sh <path to a DroneDB clone> <commit>
#
# The files come from `git archive <commit>`, so they are the committed bytes of that commit,
# whatever the checkout or its line-ending settings, except that the planning identifiers of
# DroneDB's own documents in their prose (the "(<id>, ...)" style references) are stripped:
# they mean nothing in this repository. ContractSourceTests fails when a vendored file no longer
# matches SOURCE, so a fixture changes only through this script.
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

# The prose of the README and of the schema and vector descriptions, not the fixtures
find "$tmp" -type f \( -name '*.md' -o -path '*/schemas/*.json' -o -path '*/vectors/*.json' \) -print0 |
    xargs -0 perl -0pi -e '
        my $id = qr/(?:RQ|XI|DQ|HQ|REG|UPL|HUB|DDB)-\d{2}/;
        my $any = qr/(?:$id|(?:decision\s+)?D\d)/;
        s/[ \t]*\(\s*$any(?:\s*(?:[,;\/+]|and)\s*$any)*\s*\)//g;
        1 while s/\(\s*$id\s*[,;:]\s+(?=[A-Za-z0-9`])/(/g;
        s/\s*[,;]\s*$any\s*\)/)/g;
        s/\s+of $id(?=\s*[(:])//g;
        s/ \((?:RQ|XI|DQ)\)(?=[ \t:])//g;'

rm -rf "$contract/README.md" "$contract/schemas" "$contract/fixtures" "$contract/vectors"
mkdir -p "$contract"
cp -R "$tmp/$source_dir/." "$contract/"

{
    echo "# libddb contract fixtures vendored from DroneDB. Do not edit the vendored files:"
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
