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

# Files (count) in the given pathspecs, read NUL-separated from stdin, with a line matching the pattern (PCRE).
list_files_matching() {
    local pattern="$1" specs=()
    mapfile -d '' specs
    { git grep --untracked -I -P -l -e "$pattern" -- "${specs[@]}" || true; } | wc -l | tr -d ' '
}

# Registration statements (services.AddX<...>( ) of the shared node module
# (AddProcessingNodeServices) that the web host's modules (Startup, Hosting/*ServiceCollectionExtensions) write again.
redeclared_node_registrations() {
    regs() {
        { git grep --untracked -I -h -o -P 'services\.(Add|TryAdd)[A-Za-z]+(<[^;(]*>)?\(' -- "$@" || true; } |
            tr -d ' ' | sort -u
    }
    comm -12 <(regs Registry.Web/Startup.cs ':(glob)Registry.Web/Hosting/**/*ServiceCollectionExtensions.cs') \
        <(regs Registry.Web/Utilities/ProcessingNodeServiceCollectionExtensions.cs) | wc -l | tr -d ' '
}

# Files (NUL-separated) in the given pathspecs (read NUL-separated from stdin) whose class takes an
# IFileSystem (a constructor parameter or a field).
files_with_injected_fs() {
    local specs=()
    mapfile -d '' specs
    git grep --untracked -I -P -l -z -e 'IFileSystem \w+[,)]|IFileSystem _\w+' -- "${specs[@]}" || true
}

# Asynchronous controller actions (the method after an [Http*] attribute returns a Task) whose parameters
# have no CancellationToken, in the given pathspecs (read NUL-separated from stdin). Whole signatures are
# parsed, so a parameter on a later line counts.
async_actions_without_token() {
    list_files | xargs -0 -r perl -0777 -ne '
        BEGIN { $c = 0 }
        while (/\[Http(?:Get|Post|Put|Delete|Patch)\b.*?\n\s*public\s+([^(]*?)\s\w+\s*(\((?:[^()]++|(?2))*\))/sg) {
            my ($returns, $params) = ($1, $2);
            $c++ if $returns =~ /\b(?:async|Task|ValueTask)\b/ && $params !~ /CancellationToken\s+\w+/;
        }
        END { print $c + 0, "\n" }' |
        awk '{ s += $1 } END { print s + 0 }'
}

# Controller actions (the method after an [Http*] attribute) with no [ProducesResponseType] among the
# attributes between the previous action's signature and its own, in the given pathspecs (read
# NUL-separated from stdin). Whole signatures are parsed (RQ-14).
actions_without_produces() {
    list_files | xargs -0 -r perl -0777 -ne '
        BEGIN { $c = 0 }
        my $prev = 0;
        while (/\[Http(?:Get|Post|Put|Delete|Patch|Head)\b.*?\n\s*public\s+[^(]*?\s\w+\s*(\((?:[^()]++|(?1))*\))/sg) {
            my $region = substr($_, $prev, $+[0] - $prev); $prev = $+[0];
            $c++ unless $region =~ /ProducesResponseType/;
        }
        END { print $c + 0, "\n" }' |
        awk '{ s += $1 } END { print s + 0 }'
}

# Newtonsoft attributes (RQ-14): attribute uses in the files that import Newtonsoft.Json, plus fully qualified ones.
newtonsoft_attributes() {
    list_files | xargs -0 -r perl -0777 -ne '
        my $c = 0;
        $c += () = /\[Json(?:Property|Converter|Ignore|Constructor|Object|Required|ExtensionData)\b/g if /^using Newtonsoft\.Json/m;
        $c += () = /\[Newtonsoft\.Json\.\w+/g;
        print "$c\n"' |
        awk '{ s += $1 } END { print s + 0 }'
}

# The libddb exports that honour a cancel token (DDBGetCapabilities().cancellable) and Registry calls (RQ-13).
CANCELLABLE_EXPORTS='NativeDdb\.(DDBBuild|DDBExportRaster2?|DDBGenerateMemoryThumbnail(Ex)?|DDBGenerateThumbnail|DDBMemoryTile(Ex|Fmt)?|DDBRenderRasterIndex|DDBRenderRasterRegion(Ex)?|DDBTile)\('

tests() { printf '%s\0' "${TEST_CODE[@]}"; }
paths() { printf '%s\0' "$@"; }

# The objects slice (RQ-11): the objects manager and its services, the object controllers and their ports.
OBJECTS_SLICE=(Registry.Web/Services/Managers/ObjectsManager.cs Registry.Web/Services/Ports/IObjectsManager.cs
    ':(glob)Registry.Web/Services/Objects/**/*.cs' ':(glob)Registry.Web/Controllers/Object*Controller.cs'
    Registry.Web/Controllers/RasterAnalysisController.cs Registry.Web/Controllers/StockpileController.cs)
slice() { printf '%s\0' "${OBJECTS_SLICE[@]}"; }

# Build-layout literals (XI-04, D8): libddb's build folder and file names, its marker suffixes, the build folder members.
BUILD_LAYOUT='"(cog|copc|ept|vec|mvt|nxs|gsplat|3dtiles)"|(cog\.tif|cloud\.copc\.laz|source\.gpkg|model\.nxz|model\.rad|model\.spz|ept\.json|tileset\.json|georef\.json|local\.json|bounds\.json)|\.pending\b|\.building\b|BuildFolderName|BuildFolderPath'

# The native raster calls of the objects slice (XI-06): all of them, and those made through INativeWorkGate
# (RunAsync(NativeLane.X, () => ...), or a service's own RunAsync(() => ...) over the gate).
NATIVE_RASTER='(?:GetRaster\w+|CalculateVolume|DetectStockpile|DetectAllStockpiles|GenerateContours|ExportRaster|MergeMultispectral|ValidateMergeMultispectral|PreviewMergeMultispectral|ValidateAlignRaster|GenerateThumbnailEx|GenerateTileEx|GenerateTile)'
ungated_native_calls() {
    local all gated
    all="$(slice | multiline "\\b(?:\\w+\\.)*[Dd]db\\.$NATIVE_RASTER\\(")"
    gated="$(slice | multiline "RunAsync\\((?:NativeLane\\.\\w+,\\s*)?\\(\\)\\s*=>\\s*(?:\\w+\\.)*[Dd]db\\.$NATIVE_RASTER\\(")"
    echo $((all - gated))
}

# "<class> <parameters> <optional parameters>" for every constructor of the classes of the slice.
slice_constructors() {
    slice | list_files | xargs -0 -r perl -0777 -ne 'my %cls; while (/\bclass (\w+)/g) { $cls{$1} = 1 } pos($_) = 0;
        while (/public (\w+)\(([^{;]*?)\)\s*(?::\s*(?:base|this)\([^)]*\)\s*)?\{/gs) {
            my ($name, $p) = ($1, $2); next unless $cls{$name};
            my $opt = () = $p =~ /=\s*null!?\s*(?:,|$)/g;
            $p =~ s/<[^<>]*>//g while $p =~ /<[^<>]*>/; $p =~ s/\s+//g;
            printf "%s %d %d\n", $name, ($p eq "" ? 0 : (() = $p =~ /,/g) + 1), $opt }'
}

# Files of the slice longer than 800 lines.
slice_long_files() { slice | list_files | xargs -0 -r wc -l | awk '$2 != "total" && $1 > 800' | wc -l | tr -d ' '; }

# The RQ-12 set: the classes the plan names (system, configuration, users, STAC, datasets, push, share,
# local login, the former WebUtils) and the folders their pieces move to.
RQ12_SET=(Registry.Web/Services/Managers/SystemManager.cs Registry.Web/Controllers/SystemController.cs
    Registry.Web/Services/Adapters/WebUtils.cs Registry.Web/Services/Adapters/ConfigurationDataBuilder.cs
    Registry.Web/Services/Managers/UsersManager.cs Registry.Web/Services/Managers/StacManager.cs
    Registry.Web/Services/Managers/DatasetsManager.cs Registry.Web/Services/Managers/PushManager.cs
    Registry.Web/Services/Managers/ShareManager.cs Registry.Web/Services/Managers/LocalLoginManager.cs
    Registry.Web/Controllers/ProcessingNodesController.cs Registry.Web/Controllers/FeaturesController.cs
    Registry.Web/Controllers/RemoteImportController.cs Registry.Web/Controllers/BuildMaintenanceController.cs
    ':(glob)Registry.Web/Services/RemoteImport/**/*.cs' ':(glob)Registry.Web/Services/Maintenance/**/*.cs'
    ':(glob)Registry.Web/Services/Configuration/**/*.cs' ':(glob)Registry.Web/Services/Stac/**/*.cs'
    ':(glob)Registry.Web/Services/Users/**/*.cs' ':(glob)Registry.Web/Services/Datasets/**/*.cs'
    ':(glob)Registry.Web/Services/Shares/**/*.cs' ':(glob)Registry.Web/Services/Push/**/*.cs'
    ':(glob)Registry.Web/Services/Builds/**/*.cs' ':(glob)Registry.Web/Services/Caching/**/*.cs'
    ':(glob)Registry.Web/Services/Urls/**/*.cs' ':(glob)Registry.Web/Services/Features/**/*.cs'
    ':(glob)Registry.Web/Services/Processing/**/*.cs')
rq12() { printf '%s\0' "${RQ12_SET[@]}"; }

# "<class> <parameters>" for every public constructor of the classes declared in the given files (NUL-separated on stdin).
constructors() {
    list_files | xargs -0 -r perl -0777 -ne 'my %cls; while (/\bclass (\w+)/g) { $cls{$1} = 1 } pos($_) = 0;
        while (/public (\w+)\(([^{;]*?)\)\s*(?::\s*(?:base|this)\([^)]*\)\s*)?\{/gs) {
            my ($name, $p) = ($1, $2); next unless $cls{$name};
            $p =~ s/<[^<>]*>//g while $p =~ /<[^<>]*>/; $p =~ s/\s+//g;
            printf "%s %d\n", $name, ($p eq "" ? 0 : (() = $p =~ /,/g) + 1) }'
}

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

    # Error model (RQ-05). "Managers" includes the services of the objects slice (Services/Objects, RQ-11),
    # which took over ObjectsManager's code
    m "Controller log+rethrow catch blocks"          "$(paths ':(glob)Registry.Web/Controllers/**/*.cs' |
        multiline 'catch \(Exception \w+\)\s*\{\s*_logger\.Log\w+\([^;]*;\s*throw;')"
    m "catch (Exception ...) total"                  "$(prod '' | lines 'catch \(Exception\b')"
    m "new ErrorResponse( in controllers"            "$(prod Registry.Web/Controllers | lines 'new ErrorResponse\(')"
    m "ErrorResponse references (D1)"                "$(prod Registry.Web | lines '\bErrorResponse\b')"
    m "Controller entry debug traces"                "$(paths ':(glob)Registry.Web/Controllers/**/*.cs' |
        multiline '_logger\.LogDebug\(\s*"[^"]*[Cc]ontroller')"
    m "Controller Content-Length pre-checks"         "$(prod Registry.Web/Controllers | lines 'Request\.ContentLength is \{ \} \w+ && \w+ >')"
    m "InvalidOperationException thrown by managers" "$(prod Registry.Web/Services/Managers Registry.Web/Services/Objects | lines 'throw new InvalidOperationException')"
    m "ArgumentException thrown by managers"         "$(prod Registry.Web/Services/Managers Registry.Web/Services/Objects | lines 'throw new ArgumentException\(')"
    m "UnauthorizedException thrown by managers"     "$(prod Registry.Web/Services/Managers Registry.Web/Services/Objects | lines 'throw new UnauthorizedException')"
    m "Web files importing Registry.Adapters"        "$(prod Registry.Web | list_files_matching '^using Registry\.Adapters')"

    # Access and authorization (RQ-06)
    m "RequestAccess + throw blocks"                 "$(prod Registry.Web/Services |
        multiline 'RequestAccess\(\w+, AccessType\.\w+\)\)\s*\n\s*throw new (Unauthorized|Forbidden)Exception\(')"
    m "IsUserAdmin() guard + throw blocks"           "$(prod Registry.Web/Services |
        multiline 'if \(!await _authManager\.IsUserAdmin\(\)\)\s*\n\s*throw new')"
    m "Sync _utils.GetDataset/GetOrganization calls" "$(prod Registry.Web | lines '_utils\.(GetDataset|GetOrganization)\(')"
    m "RequestAccess + throw blocks (any form)"      "$(prod Registry.Web |
        multiline 'RequestAccess\([^;]*\)\)\s*\n\s*throw new (Unauthorized|Forbidden)Exception\(')"
    m "RequestAccess calls in controllers"           "$(prod Registry.Web/Controllers | lines '\.RequestAccess\(')"
    m "IsUserAdmin() + throw (any form)"             "$(prod Registry.Web |
        multiline 'IsUserAdmin\(\)\)?[^;{\n]*\n(\s*[^;{\n]*\n)?\s*throw new')"
    m "Sync IUtils lookups (any receiver)"           "$(prod Registry.Web | lines '\b\w*[Uu]tils\.(GetDataset|GetOrganization)\(')"
    m "Raster prologue copies (EnsurePathValidity)"  "$(prod Registry.Web | lines '= EnsurePathValidity\(')"
    m "new OrganizationUser in managers"             "$(prod Registry.Web/Services/Managers Registry.Web/Services/Objects | lines 'new OrganizationUser\b')"

    # Composition root (RQ-07)
    m "GetService/GetRequiredService calls"          "$(prod '' | lines 'Get(Required)?Service<')"
    m "Console.Write* in Program+Startup"            "$(paths Registry.Web/Program.cs Registry.Web/Startup.cs | lines 'Console\.Write')"
    m ".Wait() calls"                                "$(prod '' | lines '\.Wait\(\)')"
    m "Node registrations re-declared by the web host" "$(redeclared_node_registrations)"
    m "AddIdentityCore builder copies"               "$(prod Registry.Web | lines 'AddIdentityCore<')"
    m "Hangfire storage configuration copies"        "$(prod Registry.Web | lines 'SetDataCompatibilityLevel\(')"
    m "UseSerilog call sites"                        "$(prod Registry.Web | lines '\.UseSerilog\(')"
    m "HttpContextAccessor registrations"            "$(prod Registry.Web | lines 'AddHttpContextAccessor\(|<IHttpContextAccessor, HttpContextAccessor>')"
    m "Hangfire dashboard registrations"             "$(prod Registry.Web | lines 'MapHangfireDashboard|UseHangfireDashboard')"
    m "IOptions<AppSettings> uses"                   "$(prod '' | lines 'IOptions<AppSettings>')"
    m "Files consuming IOptions<AppSettings>"        "$(prod Registry.Web | list_files_matching 'IOptions<AppSettings>')"
    m "Inline ?? new XSettings() defaults"           "$(prod Registry.Web | lines '\?\? new \w+(Settings|Options|Policy)\(\)')"
    m "Service-locator calls in Services/Initialization" "$(prod Registry.Web/Services/Initialization | lines 'Get(Required)?Service<')"
    m ".Wait() / Task.Delay in startup code"         "$(paths Registry.Web/Program.cs Registry.Web/Startup.cs \
        ':(glob)Registry.Web/Services/Initialization/**/*.cs' ':(glob)Registry.Web/Hosting/**/*.cs' |
        lines '\.Wait\(\)|Task\.Delay\(')"
    m "Console helper calls in Program+Startup"      "$(paths Registry.Web/Program.cs Registry.Web/Startup.cs | lines 'WriteLineColor\(|WriteColor\(')"
    m "HostFacts.Current() call sites (XI-06)"       "$(prod Registry.Web | lines 'HostFacts\.Current\(\)')"

    # Native interop (RQ-08)
    m "[DllImport] in production code"               "$(prod '' | lines '\[DllImport')"
    m "EntryPointNotFoundException catches"          "$(paths Registry.Adapters/DroneDB/NativeDdbWrapper.cs | lines 'catch \(EntryPointNotFoundException')"

    # Background jobs (RQ-09)
    m "Copied DDB AutomaticRetry attribute"          "$(prod Registry.Web | lines 'DelaysInSeconds = \[15, 60, 180, 600, 1800\]')"
    m "Recurring-job local WriteLine copies"         "$(prod Registry.Web | lines 'void WriteLine\(string message\)')"
    m "ReadString(JsonElement) copies"               "$(prod Registry.Web | lines 'static string\? ReadString\(JsonElement')"
    # Added for RQ-09 (2026-10-21): jobs without DroneDB objects in their arguments (D4), injected
    # Hangfire services, one console, one lenient parameter reader
    m "Recurring jobs without CancellationToken"     "$(prod Registry.Web | lines 'public async Task \w+\(PerformContext\? context = null\)')"
    m "HangfireUtils static call sites"              "$({ prod Registry.Web; paths ':(exclude,glob)**/HangfireUtils.cs'; } | lines 'HangfireUtils\.\w+\(')"
    m "JobStorage.Current uses"                      "$(prod Registry.Web | lines 'JobStorage\.Current')"
    m "context != null ? ... : Log copies"           "$(prod Registry.Web | lines 'context != null \?')"
    m "Lenient JsonElement reader copies"            "$(prod Registry.Web | lines 'static [\w<>\[\]?]+ Read(String|Bool|Long|StringArray|Paths|RawJsonArray)\(JsonElement')"
    m "Job expressions passing an IDDB (D4)"         "$(prod Registry.Web |
        multiline '(?:(?:Enqueue|Schedule)\w*\(\s*\(\)\s*=>|Expression<Action>\s+\w+\s*=\s*\(\)\s*=>)\s*(?:\w+\.)?\w+\(\s*(?:ctx\.Ddb|ddb)\s*,')"
    m "Hangfire filters taking IServiceProvider"     "$(prod Registry.Web |
        multiline '(?s)class \w+\(\s*IServiceProvider sp\b[^{]*\b(?:IApplyStateFilter|IServerFilter|IClientFilter|IElectStateFilter)\b')"
    m "UseFilter(new ...) in the Hangfire configuration" "$(prod Registry.Web | lines 'UseFilter\(new ')"
    m "Hangfire static facade calls"                 "$(prod Registry.Web | lines '\b(BackgroundJob|RecurringJob)\.(Enqueue|Schedule|AddOrUpdate|Delete|Requeue|ContinueJobWith)')"
    m "Read-after-write artifact hash copies"        "$(prod Registry.Web | lines 'static async Task<string> ComputeSha256Async')"
    m "ApplyDelta calls without verified hashes"     "$(prod Registry.Web | lines '\.ApplyDelta\((?![^;]*verified)')"
    # Added for XI-04 (2026-10-22): libddb owns the pending-build retry rule (retryAfter); no backoff
    # constant and no arithmetic on lastAttempt in Registry (it was 3 lines in PendingBuildEnqueuer)
    m "Pending-build backoff copies (XI-04)"         "$(prod '' | lines 'RetryBackoffSeconds|\.LastAttempt\s*[-+<>]|[-+<>]=?\s*\w+\.LastAttempt\b')"

    # File system, paths, clock (RQ-10)
    m "Zip-slip normalizer copies"                   "$(prod Registry.Web | lines 'private static string\?? (SafeJoin|SafeRelative)\(')"
    m "Direct Path.GetTempPath()"                    "$(prod Registry.Web | lines 'Path\.GetTempPath\(\)')"
    m "Ad-hoc backslash->slash normalizations"       "$(prod '' | lines 'Replace\(\x27\\\\\x27, \x27/\x27\)')"
    m "Static File./Directory. calls"                "$(prod Registry.Web Registry.Adapters | matches '\b(File|Directory)\.[A-Z]\w+\(')"
    m "DateTime.Now (local)"                         "$(prod '' | lines 'DateTime\.Now\b')"
    # Added for RQ-10 (2026-10-21): one path rule (libddb's), one extraction loop, one disk API
    m "Archive key normalization call sites (zip-slip)" "$(prod Registry.Web | lines '\w+\.(JoinArchiveEntry|NormalizeArchiveEntry|NormalizeArchiveKey)\(')"
    m "Archive extraction loops"                     "$(prod Registry.Web | lines 'foreach \(var \w+ in \w+\.Entries\(\)\)')"
    m "Inline reserved-path checks"                  "$({ prod Registry.Web; paths ':(exclude,glob)**/DatasetPathRules.cs' \
        ':(exclude,glob)**/UploadPathPolicy.cs' ':(exclude,glob)**/DatasetPathPolicy.cs'; } |
        lines '^(?!.*bool IsReserved).*IsReserved\w*\(')"
    m "Hand-written traversal checks"                "$(prod Registry.Web Registry.Adapters Registry.Common |
        lines 'IsPathRooted\(|\.Contains\("\.\."\)|is "\." or "\.\."|seg == "\.\."|relativePath == "\.\."')"
    m "ValidateRelativePath(s) call sites"           "$(prod Registry.Web Registry.Adapters Registry.Common |
        lines '^(?!.*static void ValidateRelativePath).*ValidateRelativePaths?\(')"
    m "Delete-and-ignore helpers"                    "$(prod Registry.Web Registry.Ports Registry.Adapters |
        lines '\b(void|bool) (TryDelete\w*|SafeDelete\w*|DeleteIfExists|SafeFolderDelete)\(')"
    m "Separator rewrites (Replace with a separator)" "$(prod '' |
        lines 'Replace\((Path\.(Alt)?DirectorySeparatorChar|\x27\\\\\x27|\x27/\x27), ?(Path\.(Alt)?DirectorySeparatorChar|\x27/\x27|\x27\\\\\x27)\)')"
    m "OS-specific path comparers"                   "$(prod '' |
        multiline 'OperatingSystem\.IsWindows\(\)\s*\?\s*String(Comparer|Comparison)\.OrdinalIgnoreCase')"
    m "OS-detection idioms other than OperatingSystem.Is*" "$(prod '' | lines 'RuntimeInformation\.IsOSPlatform|Environment\.OSVersion\.Platform')"
    m "Static File./Directory. calls in files with an injected IFileSystem" "$({ prod Registry.Web Registry.Adapters;
        paths ':(exclude)Registry.Adapters/FileSystem.cs'; } | files_with_injected_fs |
        xargs -0 -r grep -oP '\b(File|Directory)\.[A-Z]\w+\(' | wc -l | tr -d ' ')"
    m "Disk-space call sites"                        "$({ prod Registry.Web Registry.Adapters Registry.Common;
        paths ':(exclude,glob)**/VolumeInfo.cs'; } |
        lines 'VolumeInfo\.For\(|GetStorageInfo\(|GetAvailableDiskBytes\(|new DriveInfo\(|DriveInfo\.GetDrives')"

    # Cross-repository contract (XI-09): test_data is downloaded only through TestData.Url, which
    # pins the commit of DroneDB's tests/test-data.sha; a branch URL (raw/master) is unpinned.
    m "Unpinned test_data URLs"                      "$(paths ':(exclude,glob)**/ClientApp/**' \
        ':(exclude)Registry.Test.Common/TestData.cs' |
        lines '(github\.com/DroneDB/test_data/(raw|blob)/|githubusercontent\.com/(media/)?DroneDB/test_data/)(?![0-9a-f]{40}/)')"

    # Objects slice (RQ-11, 2026-10-22): no god class, one path rule by type, one download writer, every
    # native raster call through the gate, libddb's build manifest instead of its layout (XI-04, D8)
    m "Objects-slice files over 800 lines"           "$(slice_long_files)"
    m "Objects-slice constructors over 7 parameters" "$(slice_constructors | awk '$2 > 7' | wc -l | tr -d ' ')"
    m "Objects-slice optional constructor parameters" "$(slice_constructors | awk '{ s += $3 } END { print s + 0 }')"
    m "Objects-slice ?? new fallbacks"               "$(slice | lines '\?\?=?\s*new\s+\w+')"
    m "Hand-written path checks in object controllers" "$(slice | lines '^(?!.*\bprivate\b).*(?<![.\w])(ValidatePath|EnsureValidPaths?)\(')"
    m "SendDownload call sites"                      "$(slice | lines '(?<!Task<IActionResult> )SendDownload\(')"
    m "Task.Run native calls in the objects slice"   "$(slice | lines 'Task\.Run\(\(\)\s*=>\s*(?:\w+\.)*[Dd]db\.')"
    m "Native raster calls outside INativeWorkGate (objects slice)" "$(ungated_native_calls)"
    m "Build-layout literals in the objects slice"   "$(slice | matches "$BUILD_LAYOUT")"
    m "Build-layout literals in production C#"       "$(prod '' | matches "$BUILD_LAYOUT")"
    m "Per-entry build probe calls"                  "$(prod Registry.Web | lines '\.(IsBuildable|IsBuildActive|IsBuildComplete|GetBuildStatus)\(')"

    # Other large classes and shared services (RQ-12, 2026-10-22): no god class in the set, one owner for
    # queueing builds, for caching records, for cache keys and for STAC URLs (XI-09), no build layout in
    # Registry.Web (XI-04, D8)
    m "RQ-12 files over 800 lines"                   "$(rq12 | list_files | xargs -0 -r wc -l | awk '$2 != "total" && $1 > 800' | wc -l | tr -d ' ')"
    m "RQ-12 constructors over 7 parameters"         "$(rq12 | constructors | awk '$2 > 7' | wc -l | tr -d ' ')"
    m "Files injecting IUtils"                       "$(prod Registry.Web | list_files_matching '\bIUtils\b')"
    m "Build enqueue call sites outside the build scheduler" "$({ prod Registry.Web; paths ':(exclude,glob)**/QueueDdbJobs.cs' \
        ':(exclude,glob)**/BuildScheduler.cs'; } | lines '\.(EnqueueBuild|EnqueueBuildPending|ScheduleBuildPending)\(')"
    m "Pending-flag writes outside the build scheduler" "$({ prod Registry.Web; paths ':(exclude,glob)**/BuildPendingService.cs' \
        ':(exclude,glob)**/BuildScheduler.cs'; } | lines '\.SetDatasetHasPendingBuilds\(')"
    m "Build payloads with user.Id"                  "$(prod Registry.Web | lines 'new IndexPayload\([^;]*\b[Uu]ser\.Id')"
    m "PotentiallyBuildableTypes copies"             "$(prod Registry.Web | lines 'PotentiallyBuildableTypes\b')"
    m "Raw distributed-cache get-then-set sites"     "$({ prod Registry.Web/Services; paths ':(exclude,glob)**/DistributedUploadReceiptStore.cs' \
        ':(exclude,glob)**/CacheManager.cs' ':(exclude,glob)**/RecordCache.cs'; } | lines '\b(SetRecordAsync|SetAsync)\((cacheKey|key)\b')"
    m "STAC per-item rewrites"                       "$(prod Registry.Web | lines '\bRewrite(Item|Collection)\(')"
    m "STAC native calls with Registry URLs"         "$(prod Registry.Web | lines '\.(GetStac|GetStacItemCollection)\(')"
    m "Cache key formats outside CacheCategories"    "$({ prod Registry.Web; paths ':(exclude,glob)**/CacheCategories.cs'; } |
        lines '^(?!.*const string \w+Seed\b).*("(stac-|ogc-|ds-lookup:|upload-receipt-|push-session:|tasks-list)|\{(TasksListCacheSeed|CacheKey)\})')"
    m "SafeMetaManager unfiltered DdbException catches" "$(paths Registry.Web/Services/Adapters/SafeMetaManager.cs | lines 'catch \(DdbException\)')"
    m "IObjectsManager facade injections"            "$(prod Registry.Web | lines '\bIObjectsManager \w+[,)=]')"
    m "IOgcBuildArtifactPaths uses"                  "$(prod Registry.Web | lines '\bIOgcBuildArtifactPaths\b')"
    m "Build-layout literals in Registry.Web"        "$(prod Registry.Web | matches "$BUILD_LAYOUT")"

    # Managers and API contracts (RQ-12, RQ-14)
    m "Task.Run in managers"                         "$(prod Registry.Web/Services/Managers Registry.Web/Services/Objects | lines 'Task\.Run\(')"
    m "Hand-built Content-Disposition"               "$(prod Registry.Web | lines 'attachment; filename=')"

    # Cancellation (RQ-13)
    m "Async actions without a CancellationToken"    "$(prod Registry.Web/Controllers | async_actions_without_token)"
    m "Cancellable libddb export calls outside the call envelope" "$(prod Registry.Adapters | lines "= $CANCELLABLE_EXPORTS")"
    m "Hand-built ETag code in controllers"          "$(prod Registry.Web/Controllers | lines 'Headers\.ETag\s*=|Headers\.IfNoneMatch')"
    m "Cancellation answered 408"                    "$(prod Registry.Web | lines 'Status408RequestTimeout')"

    # API contracts and serialization (RQ-14)
    m "Anonymous response objects"                   "$(prod Registry.Web/Controllers | lines '\b(Ok|Accepted|Created|CreatedAtRoute|CreatedAtAction|Json)\((?!.*\bnew \{ (orgSlug|dsSlug))[^;]*\bnew \{')"
    m "Actions without [ProducesResponseType]"       "$(prod Registry.Web/Controllers | actions_without_produces)"
    m "Newtonsoft attributes in Models and Ports"    "$(prod Registry.Web/Models Registry.Ports | newtonsoft_attributes)"
    m "JToken/JObject/JArray/JValue in port signatures" "$(prod Registry.Ports Registry.Web/Services/Ports | lines '\bJ(Token|Object|Array|Value)\b')"
    m "Production files using Newtonsoft outside the allow-list" "$({ prod ""; paths ':(exclude,glob)Registry.Web.Identity/ApplicationDbContext.cs'; } | list_files_matching 'Newtonsoft')"
    m "AddNewtonsoftJson / MvcNewtonsoftJsonOptions" "$(prod "" | lines 'AddNewtonsoftJson|MvcNewtonsoftJsonOptions')"
    m "Dataset route prefix copies in controllers"   "$(prod Registry.Web/Controllers | lines 'RoutesHelper\.DatasetRadix|/ds/\{dsSlug\}')"
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
