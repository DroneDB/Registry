#!/usr/bin/env bash
# Quality metrics ratchet (RQ-03): counts of patterns the backend refactor removes may not grow.
#
# This is the CI subset of the roadmap script DroneDB-Roadmap/TBD/RegistryRefactor/_shared/
# registry_quality_metrics.sh, which stays the source of the README tables. CI cannot read the
# roadmap repository, so the metrics that must only go down are repeated here with the same
# patterns and scopes. Left out: counts expected to grow (CancellationToken, TimeProvider, ...),
# file line counts, and anything that depends on git history.
#
# The baseline (build/quality-baseline.txt) has one line per metric: "<name><TAB><value>".
# Only tracked and untracked-but-not-ignored files are counted (git grep), so build output and
# local files are never measured.
#
# Usage:
#   scripts/quality-metrics.sh            print the current values
#   scripts/quality-metrics.sh --check    compare with the baseline (CI)
#   scripts/quality-metrics.sh --update   write the current values to the baseline
#
# Exit codes: 0 no metric grew, 1 a metric grew or is missing from the baseline, 2 usage error.
set -euo pipefail
export LC_ALL=C

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASELINE="$ROOT/build/quality-baseline.txt"
cd "$ROOT"

mode="${1:-print}"
case "$mode" in
    print | --check | --update) ;;
    *)
        echo "usage: $0 [--check | --update]" >&2
        exit 2
        ;;
esac

# Production C#: no Hub, no generated migrations, no tests and no test support projects.
EXCLUDE_NON_PROD=(':(exclude,glob)**/ClientApp/**' ':(exclude,glob)**/Migrations/**'
    ':(exclude,glob)*Test/**' ':(exclude,glob)Registry.Test.Common/**'
    ':(exclude,glob)Registry.Web.TestSupport/**' ':(exclude,glob)Registry.Adapters.Testing/**')
TEST_CODE=(':(glob)Registry.Web.UnitTest/**/*.cs' ':(glob)Registry.Web.IntegrationTest/**/*.cs'
    ':(glob)Registry.Web.TestSupport/**/*.cs' ':(glob)Registry.Adapters.Testing/**/*.cs'
    ':(glob)Registry.Adapters.Test/**/*.cs' ':(glob)Registry.Adapters.Ddb.Test/**/*.cs'
    ':(glob)Registry.Test.Common/**/*.cs')

# Pathspecs of the production C# files under each directory given ("" = whole repository).
prod() {
    local dir
    for dir in "$@"; do printf '%s\0' ":(glob)${dir:+$dir/}**/*.cs"; done
    printf '%s\0' "${EXCLUDE_NON_PROD[@]}"
}

# Matching lines (PCRE) in the given pathspecs, read NUL-separated from stdin.
lines() {
    local pattern="$1" specs=()
    mapfile -d '' specs
    { git grep --untracked -I -P -c -e "$pattern" -- "${specs[@]}" || true; } |
        awk -F: '{ s += $NF } END { print s + 0 }'
}

# Matches (PCRE, several per line counted) in the given pathspecs, read NUL-separated from stdin.
matches() {
    local pattern="$1" specs=()
    mapfile -d '' specs
    { git grep --untracked -I -P -o -h -e "$pattern" -- "${specs[@]}" || true; } | wc -l | tr -d ' '
}

# Files (NUL-separated) in the given pathspecs, read NUL-separated from stdin. git grep, not
# git ls-files: with the "**/ClientApp/**" exclude, ls-files (git 2.53) lists no file at all.
list_files() {
    local specs=()
    mapfile -d '' specs
    git grep --untracked -I -l -z -e '' -- "${specs[@]}" || true
}

# Multi-line matches (Perl regex over whole files) in the given pathspecs, read NUL-separated from stdin.
multiline() {
    list_files |
        PATTERN="$1" xargs -0 -r perl -0777 -ne 'BEGIN { $c = 0 } $c += () = /$ENV{PATTERN}/g;
            END { print $c + 0, "\n" }' |
        awk '{ s += $1 } END { print s + 0 }'
}

# Files in the given pathspecs (read NUL-separated from stdin) that start with a UTF-8 BOM.
bom_files() {
    list_files | xargs -0 -r grep -l $'^\xEF\xBB\xBF' | wc -l | tr -d ' '
}

files() { list_files | tr -cd '\0' | wc -c | tr -d ' '; }

tests() { printf '%s\0' "${TEST_CODE[@]}"; }
paths() { printf '%s\0' "$@"; }

metrics() {
    m() { printf '%s\t%s\n' "$1" "$2"; }

    # Security and runtime (RQ-01, RQ-02): fixed defects that must not come back
    m "Raw X-Forwarded-For reads"                    "$(prod '' | lines '"X-Forwarded-For"')"
    m "Admin password persisted to config"           "$(prod Registry.Web/Services | lines 'DefaultAdmin\.Password = ')"
    m "Console dump of admin password"               "$(prod Registry.Web | lines 'Console\.WriteLine\(.*DefaultAdmin\.Password')"
    m "Path.GetPathRoot disk checks"                 "$(prod '' | lines 'Path\.GetPathRoot')"
    m "ServerVersion.AutoDetect calls"               "$(prod '' | lines 'ServerVersion\.AutoDetect')"

    # Tooling (RQ-03)
    m "Files with UTF-8 BOM"                         "$(paths '*.cs' '*.csproj' '*.props' '*.json' '*.yml' '*.sh' '*.md' ':(exclude,glob)**/ClientApp/**' | bom_files)"
    m "Projects/props pinning LangVersion"           "$(paths '*.csproj' '*.props' | lines '<LangVersion>')"
    m "PackageReference with inline Version (CPM)"   "$(paths '*.csproj' '*.props' | lines '<PackageReference\b[^>]*\bVersion(Override)?=')"

    # Tests (RQ-04)
    m "UseInMemoryDatabase (tests)"                  "$(tests | lines 'UseInMemoryDatabase')"
    m "new ObjectsManager( in tests"                 "$(tests | lines 'new ObjectsManager\(')"
    m "Thread.Sleep / Task.Delay in tests"           "$(tests | lines 'Thread\.Sleep|Task\.Delay')"
    m "Mock<IOptions<AppSettings>> in tests"         "$(tests | lines 'Mock<IOptions<AppSettings>>')"
    m "Test helpers in Registry.Common/Test"         "$(paths ':(glob)Registry.Common/**/Test*.cs' | files)"
    m "Category string literals (use TestCategories)" "$(tests | lines '\[(assembly: )?Category\("')"

    # Error model (RQ-05)
    m "Controller log+rethrow catch blocks"          "$(paths ':(glob)Registry.Web/Controllers/**/*.cs' |
        multiline 'catch \(Exception \w+\)\s*\{\s*_logger\.Log\w+\([^;]*;\s*throw;')"
    m "catch (Exception ...) total"                  "$(prod '' | lines 'catch \(Exception\b')"
    m "new ErrorResponse( in controllers"            "$(prod Registry.Web/Controllers | lines 'new ErrorResponse\(')"

    # Access and authorization (RQ-06)
    m "RequestAccess + throw blocks"                 "$(prod Registry.Web/Services |
        multiline 'RequestAccess\(\w+, AccessType\.\w+\)\)\s*\n\s*throw new UnauthorizedException\(')"
    m "IsUserAdmin() guard + throw blocks"           "$(prod Registry.Web/Services |
        multiline 'if \(!await _authManager\.IsUserAdmin\(\)\)\s*\n\s*throw new')"
    m "Sync _utils.GetDataset/GetOrganization calls" "$(prod Registry.Web | lines '_utils\.(GetDataset|GetOrganization)\(')"

    # Composition root (RQ-07)
    m "GetService/GetRequiredService calls"          "$(prod '' | lines 'Get(Required)?Service<')"
    m "Console.Write* in Program+Startup"            "$(paths Registry.Web/Program.cs Registry.Web/Startup.cs | lines 'Console\.Write')"
    m ".Wait() calls"                                "$(prod '' | lines '\.Wait\(\)')"

    # Native interop (RQ-08)
    m "[DllImport] in production code"               "$(prod '' | lines '\[DllImport')"
    m "EntryPointNotFoundException catches"          "$(paths Registry.Adapters/DroneDB/NativeDdbWrapper.cs | lines 'catch \(EntryPointNotFoundException')"

    # Background jobs (RQ-09)
    m "Copied DDB AutomaticRetry attribute"          "$(prod Registry.Web | lines 'DelaysInSeconds = \[15, 60, 180, 600, 1800\]')"
    m "Recurring-job local WriteLine copies"         "$(prod Registry.Web | lines 'void WriteLine\(string message\)')"
    m "ReadString(JsonElement) copies"               "$(prod Registry.Web | lines 'static string\? ReadString\(JsonElement')"

    # File system, paths, clock (RQ-10)
    m "Zip-slip normalizer copies"                   "$(prod Registry.Web | lines 'private static string\?? (SafeJoin|SafeRelative)\(')"
    m "Direct Path.GetTempPath()"                    "$(prod Registry.Web | lines 'Path\.GetTempPath\(\)')"
    m "Ad-hoc backslash->slash normalizations"       "$(prod '' | lines 'Replace\(\x27\\\\\x27, \x27/\x27\)')"
    m "Static File./Directory. calls"                "$(prod Registry.Web Registry.Adapters | matches '\b(File|Directory)\.[A-Z]\w+\(')"
    m "DateTime.Now (local)"                         "$(prod '' | lines 'DateTime\.Now\b')"

    # Cross-repository contract (XI-09): test_data is downloaded only through TestData.Url, which
    # pins the commit of DroneDB's tests/test-data.sha; a branch URL (raw/master) is unpinned.
    m "Unpinned test_data URLs"                      "$(paths ':(exclude,glob)**/ClientApp/**' \
        ':(exclude)Registry.Test.Common/TestData.cs' |
        lines '(github\.com/DroneDB/test_data/(raw|blob)/|githubusercontent\.com/(media/)?DroneDB/test_data/)(?![0-9a-f]{40}/)')"

    # Managers and API contracts (RQ-12, RQ-14)
    m "Task.Run in managers"                         "$(prod Registry.Web/Services/Managers | lines 'Task\.Run\(')"
    m "Hand-built Content-Disposition"               "$(prod Registry.Web | lines 'attachment; filename=')"
}

current="$(metrics)"

case "$mode" in
    print)
        printf '%s\n' "$current"
        exit 0
        ;;
    --update)
        {
            echo "# Quality metrics baseline for scripts/quality-metrics.sh. Do not edit by hand:"
            echo "# run scripts/quality-metrics.sh --update after a metric went down."
            echo "# Format: <metric><TAB><value>"
            printf '%s\n' "$current"
        } >"$BASELINE"
        echo "Baseline updated: $(printf '%s\n' "$current" | wc -l | tr -d ' ') metrics."
        exit 0
        ;;
esac

[[ -f "$BASELINE" ]] || { echo "error: $BASELINE not found; run $0 --update" >&2; exit 1; }

# Prints "<status>\t<metric>\t<baseline>\t<now>" with status grew, missing, dropped or same.
compare() {
    awk -F'\t' '
        FNR == NR { if ($0 !~ /^[[:space:]]*(#|$)/) base[$1] = $2; next }
        {
            if (!($1 in base))      status = "missing"
            else if ($2 > base[$1]) status = "grew"
            else if ($2 < base[$1]) status = "dropped"
            else                    status = "same"
            printf "%s\t%s\t%s\t%s\n", status, $1, ($1 in base ? base[$1] : "-"), $2
        }' "$BASELINE" - <<<"$current"
}

result="$(compare)"
failed=0

if grep -q '^dropped' <<<"$result"; then
    echo "Metrics below the baseline (<metric>: <baseline> -> <now>):"
    awk -F'\t' '$1 == "dropped" { printf "  %s: %s -> %s\n", $2, $3, $4 }' <<<"$result"
    echo "Run scripts/quality-metrics.sh --update and commit build/quality-baseline.txt, so the ratchet keeps the gain."
    if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
        echo "::warning title=Quality baseline::Metrics went down. Run scripts/quality-metrics.sh --update and commit build/quality-baseline.txt."
    fi
fi

if grep -q '^missing' <<<"$result"; then
    echo "error: metrics not in the baseline (run scripts/quality-metrics.sh --update):" >&2
    awk -F'\t' '$1 == "missing" { printf "  %s: %s\n", $2, $4 }' <<<"$result" >&2
    failed=1
fi

if grep -q '^grew' <<<"$result"; then
    echo "error: metrics above the baseline (<metric>: <baseline> -> <now>):" >&2
    awk -F'\t' '$1 == "grew" { printf "  %s: %s -> %s\n", $2, $3, $4 }' <<<"$result" >&2
    echo "Remove the new occurrences (the pattern and scope of each metric are in scripts/quality-metrics.sh)." >&2
    failed=1
fi

if [[ $failed -eq 0 ]]; then
    echo "No metric grew: $(wc -l <<<"$result" | tr -d ' ') metrics checked."
fi
exit $failed
