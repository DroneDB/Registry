#!/usr/bin/env bash
# The pinned DroneDB release (RQ-02 item 10, XI-01).
#
# Directory.Build.props is the single source of DdbVersion, DdbDebSha256 and DdbWindowsZipSha256.
# This script is how CI reads it, and it keeps the Dockerfile ARG defaults equal to it.
#
# Usage:
#   scripts/ddb-release.sh get <Property>     print a property of Directory.Build.props
#   scripts/ddb-release.sh fetch-deb <dir>    download ddb_<DdbVersion>_amd64.deb into <dir>, verify
#                                             it with sha256sum -c and print its path
#   scripts/ddb-release.sh check              fail when the Dockerfile ARG defaults differ from
#                                             Directory.Build.props, when DdbVersion is set anywhere
#                                             else, or when a build downloads releases/latest
#
# Exit codes: 0 ok, 1 check or checksum failed, 2 usage or missing property.
set -euo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROPS="$ROOT/Directory.Build.props"
DOCKERFILE="$ROOT/Dockerfile"
RELEASES_URL="https://github.com/DroneDB/DroneDB/releases/download"

# Value of <$1>...</$1> in Directory.Build.props (one line, first occurrence)
prop() {
    local value
    value="$(sed -n "s:.*<$1>\([^<]*\)</$1>.*:\1:p" "$PROPS" | head -n 1 | tr -d '\r')"
    if [[ -z "$value" ]]; then
        echo "error: <$1> is not set in Directory.Build.props" >&2
        exit 2
    fi
    printf '%s\n' "$value"
}

# Default of 'ARG $1=<value>' in the Dockerfile
dockerfile_arg() {
    sed -n "s/^ARG $1=\(.*\)$/\1/p" "$DOCKERFILE" | head -n 1 | tr -d '\r'
}

cmd_fetch_deb() {
    local dir="$1" version sha file
    version="$(prop DdbVersion)"
    sha="$(prop DdbDebSha256)"
    file="ddb_${version}_amd64.deb"
    mkdir -p "$dir"
    echo "Downloading DroneDB $version: $RELEASES_URL/v$version/$file" >&2
    curl -fsSL --retry 3 -o "$dir/$file" "$RELEASES_URL/v$version/$file"
    if ! (cd "$dir" && echo "$sha  $file" | sha256sum -c - >&2); then
        rm -f "$dir/$file"
        echo "error: $file does not match DdbDebSha256 (Directory.Build.props); deleted." >&2
        exit 1
    fi
    printf '%s\n' "$dir/$file"
}

cmd_check() {
    local failed=0 name expected actual hits

    for pair in DDB_VERSION:DdbVersion DDB_DEB_SHA256:DdbDebSha256; do
        name="${pair%%:*}"
        expected="$(prop "${pair#*:}")"
        actual="$(dockerfile_arg "$name")"
        if [[ "$actual" != "$expected" ]]; then
            echo "error: Dockerfile ARG $name=${actual:-<missing>}, Directory.Build.props ${pair#*:} is $expected" >&2
            failed=1
        fi
    done

    for pair in DdbDebSha256 DdbWindowsZipSha256; do
        if ! [[ "$(prop "$pair")" =~ ^[0-9a-f]{64}$ ]]; then
            echo "error: $pair in Directory.Build.props is not a lowercase SHA256" >&2
            failed=1
        fi
    done

    # DdbVersion is set only in Directory.Build.props
    hits="$(cd "$ROOT" && git ls-files -- '*.csproj' '*.props' '*.targets' \
        | grep -vx 'Directory.Build.props' | xargs -r grep -l '<DdbVersion>' || true)"
    if [[ -n "$hits" ]]; then
        echo "error: DdbVersion is set outside Directory.Build.props: $hits" >&2
        failed=1
    fi

    # No build downloads whatever DroneDB release is the latest
    hits="$(cd "$ROOT" && grep -ln 'DroneDB/releases/latest' Dockerfile .github/workflows/*.yml || true)"
    if [[ -n "$hits" ]]; then
        echo "error: DroneDB releases/latest is downloaded in: $hits" >&2
        failed=1
    fi

    if [[ "$failed" -ne 0 ]]; then
        exit 1
    fi
    echo "DroneDB release pin: $(prop DdbVersion), Dockerfile and Directory.Build.props agree."
}

case "${1:-}" in
    get)
        [[ $# -eq 2 ]] || { echo "usage: $0 get <Property>" >&2; exit 2; }
        prop "$2"
        ;;
    fetch-deb)
        [[ $# -eq 2 ]] || { echo "usage: $0 fetch-deb <dir>" >&2; exit 2; }
        cmd_fetch_deb "$2"
        ;;
    check)
        cmd_check
        ;;
    *)
        echo "usage: $0 get <Property> | fetch-deb <dir> | check" >&2
        exit 2
        ;;
esac
