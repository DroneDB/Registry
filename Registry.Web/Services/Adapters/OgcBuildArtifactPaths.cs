using System.Globalization;
using System.IO;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The OGC services' per-format paths of build artifacts under {dataset}/.ddb/build/{hash}/...:
/// the last copy of libddb's build layout in Registry (XI-04). The objects slice reads the build
/// manifest through the role-based <see cref="IBuildArtifactResolver"/> (RQ-11); RQ-12 moves the OGC
/// services to it once a tile request's extra native call is measured.
/// </summary>
public class OgcBuildArtifactPaths : IOgcBuildArtifactPaths
{
    private readonly IFileSystem _fs;

    public OgcBuildArtifactPaths(IFileSystem fs)
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
        Absolute(ddb.GetLocalPath(Relative(entryHash, "mvt", z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), $"{y}.pbf")));

    public string GetCogPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "cog", "cog.tif")));

    public string GetVectorQueryPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "vec", "source.gpkg")));

    public string GetCopcPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "copc", "cloud.copc.laz")));

    public string GetGsplatPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "gsplat", "model.spz")));

    public string GetGsplatLodPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "gsplat", "model.rad")));

    public string GetGsplatGeorefPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "gsplat", "georef.json")));

    public string Get3DTilesDir(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "3dtiles")));

    public string Get3DTilesTilesetPath(IDDB ddb, string entryHash) =>
        Absolute(ddb.GetLocalPath(Relative(entryHash, "3dtiles", "tileset.json")));

    public bool ArtifactExists(string fullPath) =>
        !string.IsNullOrEmpty(fullPath) && _fs.Exists(fullPath);
}
