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
    public const string ModelNexus = "model.nexus";
    public const string Model3DTiles = "model.3dtiles";
    public const string GsplatLod = "gsplat.lod";
    public const string GsplatSpz = "gsplat.spz";
    public const string GsplatBounds = "gsplat.bounds";
}
