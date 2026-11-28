#!/usr/bin/env bash
# Checks that a Registry that pins another Hub than the base branch also pins a Hub with another version.
#
# The Registry embeds the Hub it pins (Registry.Web/ClientApp) and stamps the Hub's package.json version into
# the build. A storage folder that already holds a Hub of that version keeps it, so a new Hub with the version of
# the old one is never served by an upgraded installation that does not re-extract it. The pin and the version of
# the Hub must change together.
#
#   - the Hub commit this Registry pins (the submodule entry of HEAD) is compared with the one the base branch pins;
#   - the same commit passes (nothing to compare);
#   - another commit passes only when its package.json version differs from the one of the base branch's Hub.
#
# The Hub's package.json is read from the Hub repository by commit, whatever the submodule has checked out. A
# commit the submodule does not have is fetched from its origin first.
#
# Usage: scripts/check-hub-version.sh [base-ref]      (base-ref: default origin/master)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HUB_PATH="Registry.Web/ClientApp"
BASE="${1:-origin/master}"
cd "$ROOT"

fail() { echo "error: $*" >&2; exit 1; }

[[ -e "$HUB_PATH/.git" ]] || fail "The Hub submodule $HUB_PATH is not checked out (git submodule update --init)."

# The submodule entry of a revision: the Hub commit it pins
pinned() { git rev-parse --verify --quiet "$1:$HUB_PATH" || true; }

# The base branch may be absent (a shallow clone): fetch it, then use what there is
if ! git rev-parse --verify --quiet "$BASE^{commit}" >/dev/null; then
    remote="${BASE%%/*}"; branch="${BASE#*/}"
    git fetch --quiet --depth=1 "$remote" "$branch" 2>/dev/null &&
        git branch --quiet --force "$branch-base-for-hub-check" FETCH_HEAD 2>/dev/null &&
        BASE="$branch-base-for-hub-check"
fi
git rev-parse --verify --quiet "$BASE^{commit}" >/dev/null || fail "Cannot resolve the base '$BASE' (git fetch origin master)."

head_commit="$(pinned HEAD)"
base_commit="$(pinned "$BASE")"
[[ -n "$head_commit" ]] || fail "HEAD pins no Hub ($HUB_PATH is not a submodule entry)."
[[ -n "$base_commit" ]] || fail "$BASE pins no Hub ($HUB_PATH is not a submodule entry there)."

if [[ "$head_commit" == "$base_commit" ]]; then
    echo "The Hub is the one $BASE pins ($head_commit): nothing to check."
    exit 0
fi

# package.json version of a Hub commit, fetching the commit into the submodule when it is not there
hub_version() {
    local commit="$1"
    if ! git -C "$HUB_PATH" cat-file -e "$commit^{commit}" 2>/dev/null; then
        git -C "$HUB_PATH" fetch --quiet --depth=1 origin "$commit" 2>/dev/null ||
            git -C "$HUB_PATH" fetch --quiet origin 2>/dev/null ||
            fail "Cannot fetch the Hub commit $commit from the origin of the submodule."
    fi
    git -C "$HUB_PATH" show "$commit:package.json" |
        python3 -I -c 'import json, sys; print(json.load(sys.stdin)["version"])' ||
        fail "Cannot read the version of package.json at the Hub commit $commit."
}

head_version="$(hub_version "$head_commit")"
base_version="$(hub_version "$base_commit")"

if [[ "$head_version" == "$base_version" ]]; then
    echo "error: This Registry pins Hub $head_commit and $BASE pins Hub $base_commit, and both are version $head_version." >&2
    echo "       An installation that holds Hub $base_version keeps it instead of the new one. Bump the version" >&2
    echo "       in the Hub's package.json (and package-lock.json), and pin that commit." >&2
    exit 1
fi

echo "The Hub changed from $base_version ($base_commit) to $head_version ($head_commit)."
