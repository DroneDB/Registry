using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.Testing;

/// <summary>
/// An <see cref="IDdbWrapper"/> that never loads native libddb: it replays the libddb contract
/// fixtures (XI-09, <c>Contract/</c>) recorded by DroneDB on its contract dataset
/// (<c>a.txt</c>, <c>cloud.las</c>, <c>layer.geojson</c>, <c>ortho.tif</c>, <c>sub/img.jpg</c>,
/// plus the drone image <c>DJI_0018.JPG</c>).
/// <list type="bullet">
/// <item>Any dataset folder stands for the contract dataset: <c>&lt;root&gt;</c> becomes the caller's
/// folder, <c>DDBInfo</c> depths are computed from the caller's absolute path, the normalised meta
/// ids become stable ids (<see cref="MetaId"/>), and the STAC roots and collection id become the
/// caller's.</item>
/// <item>A call is answered only when a fixture records it (or, for the per-path build status,
/// records each of its paths); the outputs are deserialized exactly like the native adapter's
/// (Json.NET defaults), so a test sees what libddb returned to Registry.</item>
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

    /// <summary>The STAC arguments of the recorded <c>DDBStac</c>/<c>DDBStacItemCollection</c> calls.</summary>
    public const string RecordedCollectionRoot = "https://hub.example/orgs/contract/ds/fixtures";

    public const string RecordedCatalogRoot = "https://hub.example";
    public const string RecordedStacId = "contract";

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
        ["ortho.tif"] = "stac-item-georaster",
        ["sub/img.jpg"] = "stac-item-image",
        [DroneImage] = "stac-item-drone-image"
    };

    private const string RasterFile = "ortho.tif";
    private static readonly JToken SurveyTag = "survey";
    private static readonly JToken Annotation = JObject.Parse("""{"note":"tree","level":2}""");
    private static readonly HashSet<EntryType> FilteredTypes = [EntryType.GeoRaster, EntryType.Image];

    // The paths indexed in the contract dataset (list fixture), for GetEntry's not-found answer
    private static readonly Lazy<HashSet<string>> IndexedPaths = new(() =>
        ContractFixture.Load("list").Output.Select(e => (string)e["path"]!).ToHashSet(StringComparer.Ordinal));

    private static readonly Lazy<EntryBuildStatus[]> BuildStatuses =
        new(() => Deserialize<EntryBuildStatus[]>(ContractFixture.Load("build-status").Output));

    public virtual string TileMimeType => "image/png";
    public virtual string ThumbnailMimeType => "image/webp";

    public virtual string GetVersion() => Version;

    /// <summary>Nothing to initialise: the fake has no native state.</summary>
    public virtual void RegisterProcess(bool verbose = false)
    {
    }

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
        foreach (var entry in output.Children<JObject>())
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

    public virtual List<Entry> List(string ddbPath, string path, bool recursive = false, int maxRecursionDepth = 0) =>
        List(ddbPath, [path], recursive, maxRecursionDepth);

    public virtual List<Entry> List(string ddbPath, string[] paths, bool recursive = false, int maxRecursionDepth = 0)
    {
        var relative = paths.Select(p => Relative(ddbPath, p)).ToArray();
        var fixture = (relative, recursive, maxRecursionDepth) switch
        {
            ([""], true, 0) => "list",
            (["sub"], false, 0) => "list-folder",
            _ => null
        };
        return Serve<List<Entry>>(fixture,
            $"List({ddbPath}, [{string.Join(", ", paths)}], recursive: {recursive}, maxRecursionDepth: {maxRecursionDepth})");
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

    public virtual EntryBuildStatus[] GetBuildStatus(string ddbPath, string[] paths)
    {
        // Each status is a fact about one path: any recorded paths, in any order, are answered
        var statuses = new EntryBuildStatus[paths.Length];
        for (var i = 0; i < paths.Length; i++)
            statuses[i] = BuildStatus(Generic(paths[i]))
                          ?? throw NoFixture($"GetBuildStatus({ddbPath}, [{string.Join(", ", paths)}])");

        return statuses;
    }

    public virtual bool IsBuildable(string ddbPath, string path) =>
        (BuildStatus(Generic(path)) ?? throw NoFixture($"IsBuildable({ddbPath}, {path})")).Buildable;

    public virtual bool IsBuildComplete(string ddbPath, string path) =>
        (BuildStatus(Generic(path)) ?? throw NoFixture($"IsBuildComplete({ddbPath}, {path})")).Complete;

    /// <summary>
    /// The contract dataset has nothing pending (<c>pending-build-info-none</c>). The
    /// <c>pending-build-info</c> fixture records another dataset: read it with
    /// <see cref="ContractFixture.Load"/>.
    /// </summary>
    public virtual PendingBuildInfo[] GetPendingBuildInfo(string ddbPath) =>
        Serve<PendingBuildInfo[]>("pending-build-info-none", $"GetPendingBuildInfo({ddbPath})");

    public virtual bool IsBuildPending(string ddbPath) => GetPendingBuildInfo(ddbPath).Length > 0;

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
        return MetaOutput(fixture, path, key, $"MetaGet({ddbPath}, {key}, {path})").ToString(Formatting.None);
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
        if (!string.IsNullOrWhiteSpace(ids) && !JsonEquals(ids, new JArray()))
            throw NoFixture(call);

        var output = ContractFixture.Load("meta-dump").Output;
        foreach (var item in output.Children<JObject>())
            item["id"] = MetaId((string)item["path"]!, (string)item["key"]!, ParseJson((string)item["data"]!));

        return Deserialize<List<MetaDump>>(output);
    }

    /// <summary>
    /// The id the fake gives a meta item in place of the normalised <c>"&lt;volatile&gt;"</c>: a UUID
    /// derived from its path, key and data, so the same item has the same id in every output.
    /// </summary>
    public static string MetaId(string path, string key, JToken data)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{path}\n{key}\n{data.ToString(Formatting.None)}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString();
    }

    // ---------------------------------------------------------------- STAC

    public virtual JToken Stac(string ddbPath, string? entry, string stacCollectionRoot, string id,
        string stacCatalogRoot)
    {
        var relative = Generic(entry);
        var call = $"Stac({ddbPath}, {entry}, {stacCollectionRoot}, {id}, {stacCatalogRoot})";
        string? fixture;
        if (relative.Length == 0)
            fixture = "stac-collection";
        else
            fixture = StacItemFixtures.GetValueOrDefault(relative);

        if (fixture == null)
            throw NoFixture(call);

        var output = WithStacRoots(ContractFixture.Load(fixture).Output, stacCollectionRoot, stacCatalogRoot);
        if (relative.Length == 0)
            WithCollectionId(output, id);
        else
            WithItemCollection(output, id);

        return Deserialize<JToken>(output);
    }

    public virtual JToken StacItemCollection(string ddbPath, string stacCollectionRoot, string id,
        string stacCatalogRoot, string? bbox, string? datetime, int limit, int offset)
    {
        var call = $"StacItemCollection({ddbPath}, {stacCollectionRoot}, {id}, {stacCatalogRoot}, {bbox}, {datetime}, {limit}, {offset})";
        if (!string.IsNullOrEmpty(bbox) || !string.IsNullOrEmpty(datetime) || limit != 10 || offset != 0)
            throw NoFixture(call);

        var output = WithStacRoots(ContractFixture.Load("stac-item-collection").Output, stacCollectionRoot,
            stacCatalogRoot);
        foreach (var feature in output["features"]?.Children<JObject>() ?? [])
            WithItemCollection(feature, id);

        return Deserialize<JToken>(output);
    }

    // ---------------------------------------------------------------- raster

    public virtual string GetRasterInfo(string path) =>
        RasterText(IsRasterFile(path) ? "raster-info" : null, $"GetRasterInfo({path})");

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
    public virtual void AppendPassword(string ddbPath, string password) => throw Unsupported();
    public virtual bool VerifyPassword(string ddbPath, string password) => throw Unsupported();
    public virtual void ClearPasswords(string ddbPath) => throw Unsupported();

    public virtual Dictionary<string, object> ChangeAttributes(string ddbPath, Dictionary<string, object> attributes) =>
        throw Unsupported();

    public virtual Dictionary<string, object> GetAttributes(string ddbPath) => throw Unsupported();
    public virtual void GenerateThumbnail(string filePath, int size, string destPath) => throw Unsupported();
    public virtual byte[] GenerateThumbnail(string filePath, int size) => throw Unsupported();

    public virtual string GenerateTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false) => throw Unsupported();

    public virtual byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false, string inputPathHash = "") => throw Unsupported();

    public virtual byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate, string inputPathHash, string outputFormat) => throw Unsupported();

    public virtual void SetTag(string ddbPath, string newTag) => throw Unsupported();
    public virtual string? GetTag(string ddbPath) => throw Unsupported();
    public virtual Stamp GetStamp(string ddbPath) => throw Unsupported();
    public virtual string GetStampChecksum(string ddbPath) => throw Unsupported();
    public virtual Delta Delta(string ddbPath, string ddbTarget) => throw Unsupported();

    public virtual List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy,
        string? sourceMetaDump = null) => throw Unsupported();

    public virtual Delta Delta(Stamp source, Stamp target) => throw Unsupported();

    public virtual Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "") =>
        throw Unsupported();

    public virtual void MoveEntry(string ddbPath, string source, string dest) => throw Unsupported();

    public virtual void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false) => throw Unsupported();

    public virtual bool IsBuildActive(string ddbPath, string path) => throw Unsupported();
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

    /// <summary>Same as the native adapter (<c>NativeJson</c>): Json.NET defaults over the JSON text.</summary>
    private static T Deserialize<T>(JToken output) =>
        JsonConvert.DeserializeObject<T>(output.ToString(Formatting.None))
        ?? throw new InvalidOperationException($"The fixture output is not a {typeof(T).Name}");

    private static JToken MetaOutput(string? fixture, string? path, string key, string call)
    {
        if (fixture == null)
            throw NoFixture(call);

        var output = ContractFixture.Load(fixture).Output;
        IEnumerable<JObject> items = output is JArray array ? array.Children<JObject>() : [(JObject)output];
        foreach (var item in items)
            item["id"] = MetaId(Generic(path), key, item["data"]!);

        return output;
    }

    private static int Removed(string? fixture, string call) =>
        fixture == null ? throw NoFixture(call) : (int)ContractFixture.Load(fixture).Output["removed"]!;

    private static string RasterText(string? fixture, string call) =>
        fixture == null ? throw NoFixture(call) : ContractFixture.Load(fixture).Output.ToString(Formatting.None);

    private static EntryBuildStatus? BuildStatus(string path) =>
        BuildStatuses.Value.FirstOrDefault(s => s.Path == path);

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

    private static JToken ParseJson(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JToken.Load(reader);
    }

    private static bool JsonEquals(string json, JToken expected)
    {
        try
        {
            return JToken.DeepEquals(ParseJson(json), expected);
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    private static JToken WithRoot(JToken output, string root) =>
        MapStrings(output, s => s.Replace(ContractFixture.RootPlaceholder, root, StringComparison.Ordinal));

    private static JToken WithStacRoots(JToken output, string collectionRoot, string catalogRoot) =>
        MapStrings(output, s =>
            s.StartsWith(RecordedCollectionRoot, StringComparison.Ordinal)
                ? collectionRoot + s[RecordedCollectionRoot.Length..]
                : s.StartsWith(RecordedCatalogRoot, StringComparison.Ordinal)
                    ? catalogRoot + s[RecordedCatalogRoot.Length..]
                    : s);

    /// <summary>libddb writes the id as the collection id, and as its title and description when the dataset has no name or readme.</summary>
    private static void WithCollectionId(JToken collection, string id)
    {
        foreach (var key in new[] { "id", "title", "description" })
            if ((string?)collection[key] == RecordedStacId)
                collection[key] = id;
    }

    private static void WithItemCollection(JToken item, string id)
    {
        if ((string?)item["collection"] == RecordedStacId)
            item["collection"] = id;
    }

    private static JToken MapStrings(JToken token, Func<string, string> map)
    {
        var tokens = token is JContainer container ? container.DescendantsAndSelf() : [token];
        foreach (var value in tokens.OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
            value.Value = map((string)value.Value!);
        return token;
    }
}
