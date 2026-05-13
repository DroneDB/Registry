using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Resolves on-disk paths to build artifacts (COG, MVT, GPKG sidecar, ...) for a given dataset entry.
/// DRY entry point used by all OGC services (WMS/WMTS/WFS/WCS/OGC API).
/// </summary>
public interface IBuildArtifactResolver
{
    /// <summary>Full path to the MVT root directory (build/{hash}/mvt/).</summary>
    string GetMvtDir(IDDB ddb, string entryHash);

    /// <summary>Full path to MVT metadata.json (build/{hash}/mvt/metadata.json).</summary>
    string GetMvtMetadataPath(IDDB ddb, string entryHash);

    /// <summary>Full path to a single MVT tile (build/{hash}/mvt/{z}/{x}/{y}.pbf).</summary>
    string GetMvtTilePath(IDDB ddb, string entryHash, int z, int x, int y);

    /// <summary>Full path to a raster COG (build/{hash}/cog/cog.tif).</summary>
    string GetCogPath(IDDB ddb, string entryHash);

    /// <summary>Full path to the GPKG vector sidecar (build/{hash}/vec/source.gpkg).</summary>
    string GetVectorQueryPath(IDDB ddb, string entryHash);

    /// <summary>Returns true if the given artifact path exists on disk.</summary>
    bool ArtifactExists(string fullPath);
}
