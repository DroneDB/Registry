using System;
using System.Collections.Frozen;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The artifact roles libddb defines (ddb.h "build manifest"): what an artifact is for,
/// never where it is. Roles are only ever added: a role not listed here passes through unchanged
/// and is ignored where it has no use.
/// </summary>
public static class ArtifactRoles
{
    public const string RasterCog = "raster.cog";
    public const string VectorGpkg = "vector.gpkg";
    public const string VectorMvt = "vector.mvt";
    public const string VectorLocal = "vector.local";
    public const string PointCloudCopc = "pointcloud.copc";
    public const string PointCloudEpt = "pointcloud.ept";
    public const string ModelBm3d = "model.bm3d";

    /// <summary>The Nexus multiresolution mesh of a model built before bm3d: libddb lists it only while there is no <see cref="ModelBm3d"/>.</summary>
    public const string ModelNexus = "model.nexus";
    public const string Model3DTiles = "model.3dtiles";
    public const string GsplatLod = "gsplat.lod";
    public const string GsplatSpz = "gsplat.spz";
    public const string GsplatBounds = "gsplat.bounds";

    /// <summary>
    /// The <c>.spz</c> a splat is exported to for other tools (<c>DDBExportSplatSpz</c>): made on demand, listed
    /// only while the file exists, never a render source.
    /// </summary>
    public const string GsplatExport = "gsplat.export";

    /// <summary>
    /// The roles whose file a task makes on request, after the build (<see cref="GsplatExport"/>): libddb lists them
    /// only while the file exists, and neither its entry types nor its manifest say which roles are made this way, so a
    /// complete build without one is told to run the export rather than that the artifact does not exist.
    /// </summary>
    public static readonly FrozenSet<string> MadeOnDemand = new[] { GsplatExport }.ToFrozenSet(StringComparer.Ordinal);
}
