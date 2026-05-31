using System.IO;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Resolves on-disk paths to deterministic build artifacts living under
/// {dataset}/.ddb/build/{hash}/...
/// Single source of truth used by OGC service managers; no duplicate path logic.
/// </summary>
public class BuildArtifactResolver : IBuildArtifactResolver
{
    private readonly IFileSystem _fs;

    public BuildArtifactResolver(IFileSystem fs)
    {
        _fs = fs;
    }

    private static string BuildBasePath =>
        CommonUtils.SafeCombine(IDDB.DatabaseFolderName, IDDB.BuildFolderName);

    private static string Relative(string entryHash, params string[] sub)
    {
        var combined = new string[sub.Length + 2];
        combined[0] = BuildBasePath;
        combined[1] = entryHash;
        for (var i = 0; i < sub.Length; i++) combined[i + 2] = sub[i];
        return CommonUtils.SafeCombine(combined);
    }

    // ddb.GetLocalPath() may return a path relative to the current working directory
    // (e.g. "./datasets/..."). Callers such as MVC PhysicalFileResult require an
    // absolute (rooted) path, so we normalize here as the single source of truth.
    private static string Absolute(string path) => Path.GetFullPath(path);

    public string GetMvtDir(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "mvt")));

    public string GetMvtMetadataPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "mvt", "metadata.json")));

    public string GetMvtTilePath(IDDB ddb, string entryHash, int z, int x, int y) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "mvt", z.ToString(), x.ToString(), $"{y}.pbf")));

    public string GetCogPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "cog", "cog.tif")));

    public string GetVectorQueryPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "vec", "source.gpkg")));

    public bool ArtifactExists(string fullPath) =>
        !string.IsNullOrEmpty(fullPath) && _fs.Exists(fullPath);
}
