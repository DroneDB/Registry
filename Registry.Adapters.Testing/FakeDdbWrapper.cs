using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.Testing;

/// <summary>
/// An <see cref="IDdbWrapper"/> that never loads native libddb: it replays the libddb contract
/// fixtures (<c>Contract/</c>) recorded by DroneDB on its contract dataset
/// (<c>a.txt</c>, <c>cloud.las</c>, <c>layer.geojson</c>, <c>ortho.tif</c>, <c>sub/img.jpg</c>,
/// plus the drone image <c>DJI_0018.JPG</c>).
/// <list type="bullet">
/// <item>Any dataset folder stands for the contract dataset: <c>&lt;root&gt;</c> becomes the caller's
/// folder, <c>DDBInfo</c> depths are computed from the caller's absolute path, the normalised meta
/// ids become stable ids (<see cref="MetaId"/>), and the STAC roots and collection id become the
/// caller's.</item>
/// <item>A call is answered only when a fixture records it (or, for the per-path build status,
/// records each of its paths); the outputs are deserialized exactly like the native adapter's
/// (<c>NativeJson</c>), so a test sees what libddb returned to Registry.</item>
/// <item>Anything else throws <see cref="NotSupportedException"/>: no default values. Configure a
/// <c>Mock&lt;FakeDdbWrapper&gt; { CallBase = true }</c> (every member is virtual) or record a new
/// fixture in DroneDB for it.</item>
/// </list>
/// The fixtures are a snapshot of one sequence of calls; the fake keeps no state (a meta add does
/// not change a later meta list).
/// </summary>
public class FakeDdbWrapper : IDdbWrapper
{
    /// <summary>The libddb version the fixtures were recorded with (<c>Contract/README.md</c>).</summary>
    public const string Version = "1.12.2";

    /// <summary>
    /// The folder name of the contract dataset: libddb's data-only Collection takes its title (and, without a
    /// readme, its description) from it when the dataset has no name.
    /// </summary>
    public const string RecordedDatasetFolder = "dataset";

    private const string DroneImage = "DJI_0018.JPG";
    private const string FilePrefix = "file://";

    /// <summary>The paths of the recorded <c>DDBAdd</c>, relative to the dataset folder.</summary>
    private static readonly string[] AddedPaths = ["a.txt", "cloud.las", "layer.geojson", "ortho.tif", "sub"];

    private static readonly Dictionary<string, string> InfoFixtures = new(StringComparer.Ordinal)
    {
        ["a.txt"] = "info-generic",
        ["ortho.tif"] = "info-georaster",
        ["sub/img.jpg"] = "info-image",
        ["cloud.las"] = "info-pointcloud",
        ["layer.geojson"] = "info-vector",
        [DroneImage] = "info-drone-image"
    };

    private static readonly Dictionary<string, string> GetFixtures = new(StringComparer.Ordinal)
    {
        ["sub/img.jpg"] = "get-image",
        ["sub"] = "get-folder",
        [DroneImage] = "get-drone-image"
    };

    private static readonly Dictionary<string, string> StacItemFixtures = new(StringComparer.Ordinal)
    {
        ["ortho.tif"] = "stac-data-item-georaster",
        ["sub/img.jpg"] = "stac-data-item-image"
    };

    private const string RasterFile = "ortho.tif";
    private static readonly JsonNode SurveyTag = JsonValue.Create("survey");
    private static readonly JsonNode Annotation = JsonNode.Parse("""{"note":"tree","level":2}""")!;
    private static readonly HashSet<EntryType> FilteredTypes = [EntryType.GeoRaster, EntryType.Image];

    // The paths indexed in the contract dataset (list fixture), for GetEntry's not-found answer
    private static readonly Lazy<HashSet<string>> IndexedPaths = new(() =>
        ContractFixture.Load("list").Output.AsArray().Select(e => (string)e!["path"]!).ToHashSet(StringComparer.Ordinal));

    // The build-artifacts fixture, one element per recorded path (<root> still in place)
    private static readonly Lazy<JsonArray> BuildArtifacts = new(() => ContractFixture.Load("build-artifacts").Output.AsArray());

    // The recorded DDBResolveBuildFile and DDBCopyBuildArtifacts calls: the built ortho.tif
    private const string BuiltHash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";
    private const string BuiltFile = "cog/cog.tif";

    public virtual string ThumbnailMimeType => "image/webp";

    public virtual string GetVersion() => Version;

    // ---------------------------------------------------------------- index

    public virtual List<Entry> Add(string ddbPath, string path, bool recursive = false) =>
        Add(ddbPath, [path], recursive);

    public virtual List<Entry> Add(string ddbPath, string[] paths, bool recursive = false)
    {
        var relative = paths.Select(p => Relative(ddbPath, p)).ToArray();
        var fixture = recursive && relative.SequenceEqual(AddedPaths, StringComparer.Ordinal) ? "add" : null;
        return Serve<List<Entry>>(fixture, $"Add({ddbPath}, [{string.Join(", ", paths)}], recursive: {recursive})");
    }

    public virtual List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0,
        bool withHash = false) => Info([path], recursive, maxRecursionDepth, withHash);

    public virtual List<Entry> Info(string[] paths, bool recursive = false, int maxRecursionDepth = 0,
        bool withHash = false)
    {
        var call = $"Info([{string.Join(", ", paths)}], recursive: {recursive}, maxRecursionDepth: {maxRecursionDepth}, withHash: {withHash})";
        if (paths.Length != 1 || recursive || maxRecursionDepth != 0 || !withHash)
            throw NoFixture(call);

        var path = Generic(paths[0]);
        var (file, fixture) = InfoFixtures.FirstOrDefault(f => path.EndsWith("/" + f.Key, StringComparison.Ordinal));
        if (fixture == null)
            throw NoFixture(call);

        var root = path[..^(file.Length + 1)];
        var output = WithRoot(ContractFixture.Load(fixture).Output, root);
        foreach (var entry in output.AsArray().OfType<JsonObject>())
            entry["depth"] = PathDepth(((string)entry["path"]!)[FilePrefix.Length..]);

        return Deserialize<List<Entry>>(output);
    }

    public virtual Entry? GetEntry(string ddbPath, string path)
    {
        var relative = Generic(path);
        if (GetFixtures.TryGetValue(relative, out var fixture))
            return Serve<Entry>(fixture, $"GetEntry({ddbPath}, {path})");

        // Not indexed in the contract dataset: libddb answers DDBERR_NOTFOUND, the adapter null
        if (!IndexedPaths.Value.Contains(relative))
            return null;

        throw NoFixture($"GetEntry({ddbPath}, {path})");
    }

    public virtual List<Entry> ListEntries(string ddbPath, EntryQuery query) =>
        Serve<List<Entry>>(IsWholeDataset(query) ? "list-entries" : null, $"ListEntries({ddbPath}, {query})");

    public virtual List<EntrySummary> ListEntrySummaries(string ddbPath, EntryQuery query)
    {
        string? fixture = null;
        if (IsWholeDataset(query))
            fixture = "list-entry-summaries";
        else if (IsRootRecursive(query) && query.NamePattern == "*.TIF" && query.Types != null &&
                 FilteredTypes.SetEquals(query.Types))
            fixture = "list-entries-filtered";

        return Serve<List<EntrySummary>>(fixture, $"ListEntrySummaries({ddbPath}, {query})");
    }

    // ---------------------------------------------------------------- build

    /// <summary>
    /// The build object of each path, from the <c>build-artifacts</c> fixture (built <c>ortho.tif</c>,
    /// unbuilt <c>layer.geojson</c> and <c>cloud.las</c>, <c>a.txt</c>, <c>sub</c>, the unindexed
    /// <c>missing.tif</c>): any recorded paths, in any order, with <c>&lt;root&gt;</c> as the caller's folder.
    /// </summary>
    public virtual EntryBuildArtifacts[] GetBuildArtifacts(string ddbPath, string[] paths)
    {
        var root = Generic(ddbPath);
        var answers = new JsonArray();
        foreach (var path in paths)
        {
            var recorded = BuildArtifacts.Value.FirstOrDefault(e => (string?)e!["path"] == Generic(path))
                           ?? throw NoFixture($"GetBuildArtifacts({ddbPath}, [{string.Join(", ", paths)}])");
            answers.Add(WithRoot(recorded.DeepClone(), root));
        }

        return Deserialize<EntryBuildArtifacts[]>(answers);
    }

    /// <summary>
    /// The recorded <c>cog/cog.tif</c> of the built <c>ortho.tif</c> (<c>resolve-build-file</c>); null,
    /// libddb's not-found answer, for a hash no entry of the contract dataset has.
    /// </summary>
    public virtual ResolvedBuildFile? ResolveBuildFile(string ddbPath, string hash, string path)
    {
        if (hash == BuiltHash && Generic(path) == BuiltFile)
            return NativeDdbWrapper.ParseResolvedBuildFile(OutputText(WithRoot(RecordedResolveBuildFile(), Generic(ddbPath))));

        if (!BuildArtifacts.Value.Any(e => (string?)e!["build"]?["hash"] == hash))
            return null;

        throw NoFixture($"ResolveBuildFile({ddbPath}, {hash}, {path})");
    }

    /// <summary>
    /// The recorded output of <c>DDBResolveBuildFile</c> (<c>resolve-build-file</c>): the path as a JSON string
    /// (libddb before the media type) or the object <c>{"path", "mediaType"}</c>; both read as the native adapter
    /// reads them.
    /// </summary>
    protected virtual JsonNode RecordedResolveBuildFile() => ContractFixture.Load("resolve-build-file").Output;

    public virtual IReadOnlyList<EntryTypeInfo> GetEntryTypes() =>
        Serve<List<EntryTypeInfo>>("entry-types", "GetEntryTypes()");

    /// <summary>
    /// libddb's path rule for any input (<see cref="EntryPathRule"/>, a copy of
    /// <c>ddb::normalizeEntryPath</c>): the golden vectors (<see cref="PathVectors"/>) pin every rule
    /// and the fake's tests run all of them, so this is the one call the fake answers without a
    /// recorded output.
    /// </summary>
    public virtual NormalizedEntryPath NormalizePath(string path, IReadOnlyCollection<string>? reservedFirstSegments = null) =>
        EntryPathRule.Normalize(path, reservedFirstSegments);

    /// <summary>The recorded copy of the built <c>ortho.tif</c> to another dataset (<c>copy-build-artifacts</c>).</summary>
    public virtual CopiedBuildArtifacts CopyBuildArtifacts(string sourceDdbPath, string destinationDdbPath, string hash) =>
        Serve<CopiedBuildArtifacts>(hash == BuiltHash ? "copy-build-artifacts" : null,
            $"CopyBuildArtifacts({sourceDdbPath}, {destinationDdbPath}, {hash})");

    /// <summary>
    /// The contract dataset has nothing pending (<c>pending-build-info-none</c>). The
    /// <c>pending-build-info</c> fixture records another dataset: read it with
    /// <see cref="ContractFixture.Load"/>.
    /// </summary>
    public virtual PendingBuildInfo[] GetPendingBuildInfo(string ddbPath) =>
        Serve<PendingBuildInfo[]>("pending-build-info-none", $"GetPendingBuildInfo({ddbPath})");

    /// <summary>As libddb answers it: a pending build that only waits for missing files does not count.</summary>
    public virtual bool IsBuildPending(string ddbPath) => GetPendingBuildInfo(ddbPath).Any(p => !p.AwaitingFiles);

    // ---------------------------------------------------------------- meta

    public virtual Meta MetaAdd(string ddbPath, string key, string data, string? path = null)
    {
        var fixture = key == "tags" && IsRoot(path) && JsonEquals(data, SurveyTag) ? "meta-add" : null;
        return Deserialize<Meta>(MetaOutput(fixture, path, key, $"MetaAdd({ddbPath}, {key}, {data}, {path})"));
    }

    public virtual Meta MetaSet(string ddbPath, string key, string data, string? path = null)
    {
        var fixture = key == "annotation" && Generic(path) == "sub/img.jpg" && JsonEquals(data, Annotation)
            ? "meta-set"
            : null;
        return Deserialize<Meta>(MetaOutput(fixture, path, key, $"MetaSet({ddbPath}, {key}, {data}, {path})"));
    }

    public virtual string? MetaGet(string ddbPath, string key, string? path = null)
    {
        var fixture = (key, Generic(path)) switch
        {
            ("annotation", "sub/img.jpg") => "meta-get",
            ("tags", "") => "meta-get-list",
            _ => null
        };
        return Compact(MetaOutput(fixture, path, key, $"MetaGet({ddbPath}, {key}, {path})"));
    }

    public virtual int MetaUnset(string ddbPath, string key, string? path = null) =>
        Removed(key == "name" && IsRoot(path) ? "meta-unset" : null, $"MetaUnset({ddbPath}, {key}, {path})");

    /// <summary>Removes the <c>"survey"</c> tag: its id is the one <see cref="MetaAdd"/> returned.</summary>
    public virtual int MetaRemove(string ddbPath, string id) =>
        Removed(id == MetaId("", "tags", SurveyTag) ? "meta-remove" : null, $"MetaRemove({ddbPath}, {id})");

    public virtual List<MetaListItem> MetaList(string ddbPath, string? path = null)
    {
        var fixture = Generic(path) switch
        {
            "" => "meta-list",
            "sub/img.jpg" => "meta-list-entry",
            _ => null
        };
        return Serve<List<MetaListItem>>(fixture, $"MetaList({ddbPath}, {path})");
    }

    public virtual List<MetaDump> MetaDump(string ddbPath, string? ids = null)
    {
        var call = $"MetaDump({ddbPath}, {ids})";
        if (!string.IsNullOrWhiteSpace(ids) && !JsonEquals(ids, new JsonArray()))
            throw NoFixture(call);

        var output = ContractFixture.Load("meta-dump").Output;
        foreach (var item in output.AsArray().OfType<JsonObject>())
            item["id"] = MetaId((string)item["path"]!, (string)item["key"]!, ParseJson((string)item["data"]!));

        return Deserialize<List<MetaDump>>(output);
    }

    /// <summary>
    /// The id the fake gives a meta item in place of the normalised <c>"&lt;volatile&gt;"</c>: a UUID
    /// derived from its path, key and data, so the same item has the same id in every output.
    /// </summary>
    public static string MetaId(string path, string key, JsonNode? data)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{path}\n{key}\n{Compact(data)}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString();
    }

    // ---------------------------------------------------------------- STAC

    public virtual JsonElement StacData(string ddbPath, string? entry)
    {
        var relative = Generic(entry);
        var call = $"StacData({ddbPath}, {entry})";
        var fixture = relative.Length == 0 ? "stac-data-collection" : StacItemFixtures.GetValueOrDefault(relative);
        if (fixture == null)
            throw NoFixture(call);

        var output = ContractFixture.Load(fixture).Output;
        if (relative.Length == 0)
            WithDatasetFolder(output, ddbPath);

        return Deserialize<JsonElement>(output);
    }

    /// <summary>The recorded item collection (every entry with a geometry), paged as libddb pages it.</summary>
    public virtual JsonElement StacItemCollectionData(string ddbPath, string? bbox, string? datetime, int limit, int offset)
    {
        var call = $"StacItemCollectionData({ddbPath}, {bbox}, {datetime}, {limit}, {offset})";
        if (!string.IsNullOrEmpty(bbox) || !string.IsNullOrEmpty(datetime))
            throw NoFixture(call);

        var output = ContractFixture.Load("stac-data-item-collection").Output.AsObject();
        var page = new JsonArray(output["features"]!.AsArray().Skip(Math.Max(offset, 0)).Take(limit <= 0 ? 10 : limit)
            .Select(f => f!.DeepClone()).ToArray());
        output["features"] = page;
        output["numberReturned"] = page.Count;
        return Deserialize<JsonElement>(output);
    }

    // ---------------------------------------------------------------- raster

    public virtual string GetRasterInfo(string path) =>
        RasterText(IsRasterFile(path) ? "raster-info" : null, $"GetRasterInfo({path})");

    /// <summary>
    /// The <c>raster-info</c> fixture (the RGB contract raster, whose <c>indexLayer</c> is empty) with
    /// <paramref name="indexLayer"/> as the spectral indices libddb lists for the raster: for a test that
    /// overrides <see cref="GetRasterInfo"/> to answer for a raster the fixtures do not record (a multispectral one).
    /// </summary>
    public static string RasterInfo(params string[] indexLayer)
    {
        var output = ContractFixture.Load("raster-info").Output.AsObject();
        output[RasterIndexLayer.Member] = new JsonArray(indexLayer.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        return Compact(output);
    }

    public virtual string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null)
    {
        var fixture = !IsRasterFile(path)
            ? null
            : (formula ?? "", bandFilter ?? "") switch
            {
                ("", "") => "raster-metadata",
                ("VARI", "RGB") => "raster-metadata-formula",
                _ => null
            };
        return RasterText(fixture, $"GetRasterMetadata({path}, {formula}, {bandFilter})");
    }

    // ---------------------------------------------------------------- not recorded by any fixture

    public virtual string Init(string directory) => throw Unsupported();

    public virtual BatchAddResult AddWithOptions(string ddbPath, string[] paths, bool stopOnError = false) =>
        throw Unsupported();

    public virtual BatchAddResult AddWithHints(string ddbPath, IReadOnlyList<AddItem> files, bool stopOnError = false) =>
        throw Unsupported();

    public virtual void Remove(string ddbPath, string path) => throw Unsupported();
    public virtual void Remove(string ddbPath, string[] paths) => throw Unsupported();
    public virtual int ReleaseConnections(string path) => throw Unsupported();
    public virtual bool Checkpoint(string ddbPath) => throw Unsupported();
    public virtual byte[] GenerateThumbnail(string filePath, int size) => throw Unsupported();

    public virtual byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate, string inputPathHash, string outputFormat) => throw Unsupported();

    public virtual Stamp GetStamp(string ddbPath) => throw Unsupported();
    public virtual string GetStampChecksum(string ddbPath) => throw Unsupported();

    public virtual List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy,
        string? sourceMetaDump, IReadOnlyDictionary<string, string> verifiedHashes) => throw Unsupported();

    public virtual Delta Delta(Stamp source, Stamp target) => throw Unsupported();

    public virtual Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "") =>
        throw Unsupported();

    public virtual void MoveEntry(string ddbPath, string source, string dest) => throw Unsupported();

    public virtual void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false, BuildOptions? options = null) => throw Unsupported();

    public virtual DdbCleanupResult Cleanup(string ddbPath) => throw Unsupported();
    public virtual List<RescanResult> RescanIndex(string ddbPath, RescanOptions? options = null) => throw Unsupported();

    public virtual byte[] GenerateThumbnailEx(string filePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null) => throw Unsupported();

    public virtual byte[] GenerateMemoryTileEx(string inputPath, int tz, int tx, int ty,
        int tileSize, bool tms, bool forceRecreate, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null) => throw Unsupported();

    public virtual string ValidateMergeMultispectral(string[] paths) => throw Unsupported();

    public virtual byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512) =>
        throw Unsupported();

    public virtual void MergeMultispectral(string[] paths, string outputCog) => throw Unsupported();
    public virtual string ValidateAlignRaster(string sourcePath, string referencePath) => throw Unsupported();

    public virtual string AlignRaster(string sourcePath, string referencePath, string outputPath,
        string mode = "similarity") => throw Unsupported();

    public virtual void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null) => throw Unsupported();

    public virtual void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale,
        int tileSize, Action<double, string?>? progress,
        CancellationToken cancellationToken) => throw Unsupported();

    public virtual SplatTrainingExport ExportSplatTrainingData(string ddbPath, string folder, string outFolder,
        SplatTrainingOptions? options, Action<double, string?>? progress, CancellationToken cancellationToken) =>
        throw Unsupported();

    public virtual SplatSpzExport ExportSplatSpz(string ddbPath, string entryPath,
        CancellationToken cancellationToken) => throw Unsupported();

    public virtual string GetRasterValueInfo(string path) => throw Unsupported();
    public virtual string GetRasterPointValue(string path, int x, int y) => throw Unsupported();
    public virtual string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1) => throw Unsupported();
    public virtual string GetRasterProfile(string path, string geoJsonLineString, int samples) => throw Unsupported();

    public virtual string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation) =>
        throw Unsupported();

    public virtual string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity) =>
        throw Unsupported();

    public virtual string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults) =>
        throw Unsupported();

    public virtual string GenerateContours(string path, double? interval, int? count, double baseOffset = 0.0,
        double? minElev = null, double? maxElev = null, double simplifyTolerance = 0.0, int bandIndex = 1) =>
        throw Unsupported();

    public virtual void MaskBorders(string input, string output, int nearDist = 15, bool white = false) =>
        throw Unsupported();

    public virtual byte[] RenderRasterRegion(string inputPath, double[] bbox, string bboxSrs,
        int width, int height, string format, int[]? bands = null, string? outputCrs = null) => throw Unsupported();

    public virtual byte[] RenderRasterIndex(string inputPath, string indexName, double[] bbox,
        string bboxSrs, int width, int height, string format) => throw Unsupported();

    public virtual string QueryRasterPoint(string inputPath, double x, double y, string? srs = null) =>
        throw Unsupported();

    public virtual string QueryVector(string vectorPath, VectorQuery query) => throw Unsupported();
    public virtual long CountVector(string vectorPath, VectorQuery query) => throw Unsupported();
    public virtual string DescribeVector(string vectorPath, string? layerName = null) => throw Unsupported();
    public virtual string MakeCadPrj(string crs) => throw Unsupported();

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// libddb's depth of an absolute path (<c>io::Path::depth</c>): its folders, without the root.
    /// </summary>
    public static int PathDepth(string absolutePath)
    {
        var segments = Generic(absolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var count = segments.Length > 0 && segments[0].Length == 2 && segments[0][1] == ':'
            ? segments.Length - 1 // a Windows drive ("C:") is the root name, not a folder
            : segments.Length;
        return Math.Max(count - 1, 0);
    }

    private static NotSupportedException NoFixture(string call) =>
        new($"FakeDdbWrapper: no libddb contract fixture records {call}. The recorded calls are listed in " +
            "Registry.Adapters.Testing/Contract/README.md; set the call up on a Mock<FakeDdbWrapper> " +
            "{ CallBase = true } or record a fixture in DroneDB tests/contract.");

    private static NotSupportedException Unsupported([CallerMemberName] string method = "") =>
        new($"FakeDdbWrapper: IDdbWrapper.{method} has no libddb contract fixture. Set it up on a " +
            "Mock<FakeDdbWrapper> { CallBase = true } (or a Mock<IDdbWrapper>), or record a fixture in " +
            "DroneDB tests/contract and serve it here.");

    private static T Serve<T>(string? fixture, string call) =>
        fixture == null ? throw NoFixture(call) : Deserialize<T>(ContractFixture.Load(fixture).Output);

    /// <summary>Same as the native adapter: <see cref="NativeJson"/>'s contract and options over the JSON.</summary>
    private static T Deserialize<T>(JsonNode output) =>
        output.Deserialize<T>(NativeJson.Options)
        ?? throw new InvalidOperationException($"The fixture output is not a {typeof(T).Name}");

    private static string Compact(JsonNode? node) => node?.ToJsonString(NativeJson.Options) ?? "null";

    // The text libddb returned for a recorded output: a string output is the string itself, not its JSON literal
    private static string OutputText(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : Compact(node);

    private static JsonNode MetaOutput(string? fixture, string? path, string key, string call)
    {
        if (fixture == null)
            throw NoFixture(call);

        var output = ContractFixture.Load(fixture).Output;
        IEnumerable<JsonObject> items = output is JsonArray array ? array.OfType<JsonObject>() : [output.AsObject()];
        foreach (var item in items)
            item["id"] = MetaId(Generic(path), key, item["data"]);

        return output;
    }

    private static int Removed(string? fixture, string call) =>
        fixture == null ? throw NoFixture(call) : (int)ContractFixture.Load(fixture).Output["removed"]!;

    private static string RasterText(string? fixture, string call) =>
        fixture == null ? throw NoFixture(call) : Compact(ContractFixture.Load(fixture).Output);

    private static bool IsRasterFile(string path)
    {
        var generic = Generic(path);
        return generic == RasterFile || generic.EndsWith("/" + RasterFile, StringComparison.Ordinal);
    }

    private static bool IsRootRecursive(EntryQuery query) => Generic(query.Path).Length == 0 && query.Recursive;

    private static bool IsWholeDataset(EntryQuery query) =>
        IsRootRecursive(query) && string.IsNullOrEmpty(query.NamePattern) && query.Types is not { Count: > 0 };

    private static bool IsRoot(string? path) => Generic(path).Length == 0;

    private static string Generic(string? path) => (path ?? "").Replace('\\', '/').TrimEnd('/');

    /// <summary>A dataset-relative path: <paramref name="path"/> may be absolute (under the dataset folder) or relative.</summary>
    private static string Relative(string ddbPath, string path)
    {
        var root = Generic(ddbPath);
        var generic = Generic(path);
        if (generic == root)
            return "";
        return generic.StartsWith(root + "/", StringComparison.Ordinal) ? generic[(root.Length + 1)..] : generic;
    }

    private static JsonNode? ParseJson(string json) => JsonNode.Parse(json);

    private static bool JsonEquals(string json, JsonNode expected)
    {
        try
        {
            return JsonNode.DeepEquals(ParseJson(json), expected);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode WithRoot(JsonNode output, string root) =>
        MapStrings(output, s => s.Replace(ContractFixture.RootPlaceholder, root, StringComparison.Ordinal))!;

    /// <summary>libddb names an unnamed Collection after its dataset folder: the folder of the dataset asked about.</summary>
    private static void WithDatasetFolder(JsonNode collection, string ddbPath)
    {
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(ddbPath));
        foreach (var key in new[] { "title", "description" })
            if ((string?)collection[key] == RecordedDatasetFolder)
                collection[key] = folder;
    }

    /// <summary>A copy of <paramref name="node"/> with every string value (not the member names) mapped.</summary>
    private static JsonNode? MapStrings(JsonNode? node, Func<string, string> map) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, MapStrings(p.Value, map)))),
        JsonArray a => new JsonArray(a.Select(e => MapStrings(e, map)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var text) => JsonValue.Create(map(text)),
        _ => node?.DeepClone()
    };
}
