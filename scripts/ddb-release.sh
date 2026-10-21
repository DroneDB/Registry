#!/usr/bin/env bash
# The pinned DroneDB build (RQ-02 item 10, XI-01).
#
# Directory.Build.props is the single source of "which libddb": DdbSource, DdbVersion, the CI
# coordinates of an unreleased build, and the SHA-256 of the .deb and of the Windows zip. This
# script is how CI, the Dockerfile and the Windows embed (Registry.Web.csproj) read it, and how
# they download that exact build. Nothing downloads "the latest" DroneDB.
#
# DdbSource:
#   release  a published DroneDB release: github.com/DroneDB/DroneDB/releases/download/v<DdbVersion>
#   ci       a DroneDB CI build (c-cpp.yml) of commit DdbCiCommit, run DdbCiRunId: the artifacts
#            DdbDebArtifactId (Linux-Binaries) and DdbWindowsArtifactId (Windows Binaries). GitHub
#            serves Actions artifacts only to an authenticated caller, so a token is needed:
#              DRONEDB_ARTIFACT_TOKEN   (CI: the repository secret of the same name), or
#              /run/secrets/dronedb_artifact_token   (Docker: --secret id=dronedb_artifact_token), or
#              `gh auth token`          (a developer logged in with the GitHub CLI).
#            The token needs read access to the Actions artifacts of DroneDB/DroneDB (fine-grained
#            PAT: repository DroneDB/DroneDB, "Actions: read"; classic PAT: public_repo).
#            Artifacts expire 90 days after their run: move the pin to a newer build or a release.
#
# Either way the download is checked with sha256sum -c against DdbDebSha256 / DdbWindowsZipSha256.
#
# Developer override, never used by CI: DDB_LOCAL_BUILD
#   a .deb file        fetch-deb uses it instead of the pinned .deb (not checksum-verified)
#   a .zip file        fetch-windows-zip (and so the Windows embed) uses it (not checksum-verified)
#   a DroneDB build    dev-env prints the environment that runs Registry and its tests on that
#   folder             build's libddb.so (LD_LIBRARY_PATH, DDB_DATA), e.g.
#                        eval "$(DDB_LOCAL_BUILD=../DroneDB/build scripts/ddb-release.sh dev-env)"
#
# Usage:
#   scripts/ddb-release.sh get <Property>        print a property of Directory.Build.props
#   scripts/ddb-release.sh describe              one line naming the pinned build
#   scripts/ddb-release.sh fetch-deb <dir>       download the pinned ddb_<DdbVersion>_amd64.deb (CI
#                                                build: ddb_<DdbDebFileVersion>_amd64.deb when set) into
#                                                <dir>, verify it and print its path
#   scripts/ddb-release.sh fetch-windows-zip <dir>
#                                                the same for ddb-<DdbVersion>-Windows.zip
#   scripts/ddb-release.sh fetch-ci-artifact <artifact-id> <dir>
#                                                download and unzip one DroneDB Actions artifact
#   scripts/ddb-release.sh latest-ci-artifact <branch> <name>
#                                                "<artifact-id> <run-id> <head-sha> <conclusion>" of
#                                                the newest completed c-cpp.yml run of <branch> that
#                                                still has artifact <name> (the nightly drift job)
#   scripts/ddb-release.sh dev-env               see DDB_LOCAL_BUILD above
#   scripts/ddb-release.sh check                 fail when the pin is malformed, when DdbVersion is set
#                                                anywhere else, when the Dockerfile does not install
#                                                DroneDB through fetch-deb, or when a build downloads
#                                                releases/latest
#
# Exit codes: 0 ok, 1 check, download or checksum failed, 2 usage or missing property.
set -euo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROPS="$ROOT/Directory.Build.props"
DOCKERFILE="$ROOT/Dockerfile"
REPO="DroneDB/DroneDB"
RELEASES_URL="https://github.com/$REPO/releases/download"
API_URL="https://api.github.com/repos/$REPO"
TOKEN_FILE="/run/secrets/dronedb_artifact_token"

die() {
    echo "error: $*" >&2
    exit 1
}

# Value of <$1>...</$1> in Directory.Build.props, or nothing when it is not set
optional_prop() {
    sed -n "s:.*<$1>\([^<]*\)</$1>.*:\1:p" "$PROPS" | head -n 1 | tr -d '\r'
}

# Value of <$1>...</$1> in Directory.Build.props (one line, first occurrence)
prop() {
    local value
    value="$(optional_prop "$1")"
    if [[ -z "$value" ]]; then
        echo "error: <$1> is not set in Directory.Build.props" >&2
        exit 2
    fi
    printf '%s\n' "$value"
}

describe() {
    local version
    version="$(prop DdbVersion)"
    case "$(prop DdbSource)" in
        release) echo "DroneDB $version (release v$version)" ;;
        ci) echo "DroneDB $version, CI build of commit $(prop DdbCiCommit) (run https://github.com/$REPO/actions/runs/$(prop DdbCiRunId))" ;;
        *) echo "error: DdbSource must be release or ci, found '$(prop DdbSource)'" >&2; exit 2 ;;
    esac
}

# The token for the Actions artifacts of DroneDB/DroneDB, or a failure that says how to provide one
ci_token() {
    if [[ -n "${DRONEDB_ARTIFACT_TOKEN:-}" ]]; then
        printf '%s' "$DRONEDB_ARTIFACT_TOKEN"
    elif [[ -s "$TOKEN_FILE" ]]; then
        tr -d '\r\n' < "$TOKEN_FILE"
    elif command -v gh >/dev/null 2>&1 && gh auth token >/dev/null 2>&1; then
        gh auth token
    else
        echo "error: DRONEDB_ARTIFACT_TOKEN is not set. Registry pins an unreleased DroneDB build ($(describe)), and GitHub serves Actions artifacts only with a token. In GitHub Actions add the repository secret DRONEDB_ARTIFACT_TOKEN (a token with Actions read access to DroneDB/DroneDB; see scripts/ddb-release.sh); for docker build pass --secret id=dronedb_artifact_token,env=DRONEDB_ARTIFACT_TOKEN; locally set DRONEDB_ARTIFACT_TOKEN, log in with gh, or set DDB_LOCAL_BUILD." >&2
        exit 1
    fi
}

# Downloads and unzips Actions artifact $1 of DroneDB/DroneDB into $2
fetch_ci_artifact() {
    local id="$1" dir="$2" token zip status
    [[ "$id" =~ ^[0-9]+$ ]] || die "artifact id must be a number, found '$id'"
    token="$(ci_token)"
    mkdir -p "$dir"
    zip="$(mktemp "${TMPDIR:-/tmp}/ddb-artifact-XXXXXX.zip")"
    echo "Downloading DroneDB CI artifact $id" >&2
    # -L: the API redirects to a signed storage URL; curl does not forward the Authorization
    # header to that other host
    status="$(curl -sSL --retry 3 -o "$zip" -w '%{http_code}' \
        -H "Authorization: Bearer $token" -H "Accept: application/vnd.github+json" \
        "$API_URL/actions/artifacts/$id/zip" || true)"
    if [[ "$status" != "200" ]]; then
        rm -f "$zip"
        case "$status" in
            401|403) die "DroneDB artifact $id: HTTP $status, the token cannot read the Actions artifacts of $REPO (DRONEDB_ARTIFACT_TOKEN needs Actions: read on $REPO)" ;;
            404) die "DroneDB artifact $id: HTTP 404, no such artifact in $REPO (or the token cannot see it)" ;;
            410) die "DroneDB artifact $id: HTTP 410, the artifact expired (90 days after its run): pin a newer DroneDB CI build or a release in Directory.Build.props" ;;
            *) die "DroneDB artifact $id: download failed (HTTP ${status:-none})" ;;
        esac
    fi
    command -v unzip >/dev/null 2>&1 || { rm -f "$zip"; die "unzip is required to extract a DroneDB CI artifact"; }
    unzip -q -o "$zip" -d "$dir"
    rm -f "$zip"
}

# Verifies $1/$2 against SHA-256 $3 (property name $4); deletes the file when it does not match
verify() {
    local dir="$1" file="$2" sha="$3" name="$4"
    [[ -f "$dir/$file" ]] || die "$file is not in the downloaded DroneDB build ($(describe))"
    if ! (cd "$dir" && echo "$sha  $file" | sha256sum -c - >&2); then
        rm -f "$dir/$file"
        die "$file does not match $name (Directory.Build.props); deleted."
    fi
}

# The version in the .deb file name: DdbVersion, except for a CI build whose .deb is named after
# the last DroneDB tag (DdbDebFileVersion; DroneDB names a non-tag .deb after `git describe`)
deb_file_version() {
    local version
    version="$(optional_prop DdbDebFileVersion)"
    if [[ -n "$version" && "$(prop DdbSource)" == "ci" ]]; then
        printf '%s\n' "$version"
    else
        prop DdbVersion
    fi
}

# Downloads the pinned file of kind $1 (deb|windows) into $2, verifies it and prints its path
fetch_pinned() {
    local kind="$1" dir="$2" version file sha_prop artifact_prop local_ext
    version="$(prop DdbVersion)"
    case "$kind" in
        deb) file="ddb_$(deb_file_version)_amd64.deb"; sha_prop=DdbDebSha256; artifact_prop=DdbDebArtifactId; local_ext=deb ;;
        windows) file="ddb-${version}-Windows.zip"; sha_prop=DdbWindowsZipSha256; artifact_prop=DdbWindowsArtifactId; local_ext=zip ;;
    esac
    mkdir -p "$dir"

    if [[ -n "${DDB_LOCAL_BUILD:-}" ]]; then
        [[ "$DDB_LOCAL_BUILD" == *."$local_ext" && -f "$DDB_LOCAL_BUILD" ]] ||
            die "DDB_LOCAL_BUILD=$DDB_LOCAL_BUILD is not a .$local_ext file"
        echo "warning: DDB_LOCAL_BUILD: using $DDB_LOCAL_BUILD instead of the pinned build ($(describe)); it is not checksum-verified" >&2
        cp -f "$DDB_LOCAL_BUILD" "$dir/$(basename "$DDB_LOCAL_BUILD")"
        printf '%s\n' "$dir/$(basename "$DDB_LOCAL_BUILD")"
        return
    fi

    case "$(prop DdbSource)" in
        release)
            echo "Downloading $(describe): $RELEASES_URL/v$version/$file" >&2
            curl -fsSL --retry 3 -o "$dir/$file" "$RELEASES_URL/v$version/$file"
            ;;
        ci)
            echo "Downloading $(describe)" >&2
            fetch_ci_artifact "$(prop "$artifact_prop")" "$dir"
            ;;
        *)
            describe >/dev/null
            ;;
    esac
    verify "$dir" "$file" "$(prop "$sha_prop")" "$sha_prop"
    printf '%s\n' "$dir/$file"
}

cmd_latest_ci_artifact() {
    local branch="$1" name="$2" token runs run sha conclusion found
    command -v jq >/dev/null 2>&1 || die "jq is required"
    token="$(ci_token)"
    api() { curl -fsSL --retry 3 -H "Authorization: Bearer $token" -H "Accept: application/vnd.github+json" "$API_URL/$1"; }
    # Completed runs of the branch, newest first, whatever their conclusion: a run whose later jobs
    # failed still carries a .deb that passed its own build job's tests. Runs from forks are never
    # used (their .deb would be installed with sudo).
    runs="$(api "actions/workflows/c-cpp.yml/runs?branch=$branch&status=completed&per_page=20" |
        jq -r '.workflow_runs[] | select(.head_repository.id == .repository.id) | "\(.id) \(.head_sha) \(.conclusion)"')"
    while read -r run sha conclusion; do
        [[ -n "$run" ]] || continue
        found="$(api "actions/runs/$run/artifacts?name=$(jq -rn --arg n "$name" '$n|@uri')" |
            jq -r '[.artifacts[] | select(.expired | not)][0].id // empty')"
        if [[ -n "$found" ]]; then
            echo "$found $run $sha $conclusion"
            return
        fi
    done <<< "$runs"
    die "no completed c-cpp.yml run of DroneDB $branch has an unexpired $name artifact"
}

# The environment that runs Registry on the libddb.so of a DroneDB build folder (DDB_LOCAL_BUILD)
cmd_dev_env() {
    local build="${DDB_LOCAL_BUILD:-}" env_dir f
    [[ -n "$build" && -f "$build/libddb.so" ]] ||
        die "set DDB_LOCAL_BUILD to a DroneDB build folder (the one with libddb.so)"
    build="$(cd "$build" && pwd)"
    env_dir="${TMPDIR:-/tmp}/registry-ddb-local"
    mkdir -p "$env_dir/lib" "$env_dir/share/ddb"
    ln -sfn "$build/libddb.so" "$env_dir/lib/libddb.so"
    # The data files libddb looks for next to an installed library (DDB_DATA)
    for f in proj.db sensor_data.sqlite timezone21.bin curl-ca-bundle.crt sensor-profiles.json; do
        if [[ -f "$build/$f" ]]; then
            cp -f "$build/$f" "$env_dir/share/ddb/"
        else
            echo "warning: $build/$f is missing" >&2
        fi
    done
    printf 'export LD_LIBRARY_PATH=%q\n' "$env_dir/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    printf 'export DDB_DATA=%q\n' "$env_dir/share/ddb"
}

cmd_check() {
    local failed=0 name hits source

    fail() {
        echo "error: $*" >&2
        failed=1
    }

    source="$(prop DdbSource)"
    case "$source" in
        release)
            [[ -z "$(optional_prop DdbDebFileVersion)" ]] || fail "DdbDebFileVersion is for a CI build only: a release names its .deb after DdbVersion"
            ;;
        ci)
            [[ "$(prop DdbCiCommit)" =~ ^[0-9a-f]{40}$ ]] || fail "DdbCiCommit must be a full lowercase commit SHA"
            for name in DdbCiRunId DdbDebArtifactId DdbWindowsArtifactId; do
                [[ "$(prop "$name")" =~ ^[0-9]+$ ]] || fail "$name must be a number"
            done
            ;;
        *) fail "DdbSource must be release or ci, found '$source'" ;;
    esac

    [[ "$(prop DdbVersion)" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "DdbVersion must be major.minor.patch"
    [[ "$(deb_file_version)" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "DdbDebFileVersion must be major.minor.patch"

    for name in DdbDebSha256 DdbWindowsZipSha256; do
        [[ "$(prop "$name")" =~ ^[0-9a-f]{64}$ ]] || fail "$name in Directory.Build.props is not a lowercase SHA256"
    done

    # The pin is written only in Directory.Build.props
    hits="$(cd "$ROOT" && git ls-files -- '*.csproj' '*.props' '*.targets' \
        | grep -vx 'Directory.Build.props' | xargs -r grep -lE '<Ddb(Version|DebFileVersion|Source|CiCommit|CiRunId|DebArtifactId|WindowsArtifactId|DebSha256|WindowsZipSha256)>' || true)"
    [[ -z "$hits" ]] || fail "the DroneDB pin is set outside Directory.Build.props: $hits"

    # The image installs the pinned build, through this script
    grep -q 'ddb-release.sh fetch-deb' "$DOCKERFILE" || fail "the Dockerfile does not install DroneDB with scripts/ddb-release.sh fetch-deb"

    # No build downloads whatever DroneDB release is the latest
    hits="$(cd "$ROOT" && grep -ln 'DroneDB/releases/latest' Dockerfile .github/workflows/*.yml Registry.Web/Registry.Web.csproj || true)"
    [[ -z "$hits" ]] || fail "DroneDB releases/latest is downloaded in: $hits"

    if [[ "$failed" -ne 0 ]]; then
        exit 1
    fi
    echo "DroneDB pin: $(describe)."
}

usage() {
    echo "usage: $0 get <Property> | describe | fetch-deb <dir> | fetch-windows-zip <dir> | fetch-ci-artifact <artifact-id> <dir> | latest-ci-artifact <branch> <name> | dev-env | check" >&2
    exit 2
}

case "${1:-}" in
    get) [[ $# -eq 2 ]] || usage; prop "$2" ;;
    describe) [[ $# -eq 1 ]] || usage; describe ;;
    fetch-deb) [[ $# -eq 2 ]] || usage; fetch_pinned deb "$2" ;;
    fetch-windows-zip) [[ $# -eq 2 ]] || usage; fetch_pinned windows "$2" ;;
    fetch-ci-artifact) [[ $# -eq 3 ]] || usage; fetch_ci_artifact "$2" "$3" ;;
    latest-ci-artifact) [[ $# -eq 3 ]] || usage; cmd_latest_ci_artifact "$2" "$3" ;;
    dev-env) [[ $# -eq 1 ]] || usage; cmd_dev_env ;;
    check) [[ $# -eq 1 ]] || usage; cmd_check ;;
    *) usage ;;
esac
