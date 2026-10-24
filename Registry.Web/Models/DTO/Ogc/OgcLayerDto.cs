using System.Collections.Generic;
using System.Text.Json.Serialization;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.DTO.Ogc;

/// <summary>
/// Single feature-class / coverage / layer exposed via the OGC suite.
/// </summary>
public class OgcLayerDto
{
    /// <summary>Canonical layer name (entry.path or entry.path:innerLayer).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Human-readable title (falls back to Name).</summary>
    public string Title { get; set; } = string.Empty;
    public string? Abstract { get; set; }
    public EntryType EntryType { get; set; }
    public string EntryPath { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
    /// <summary>Optional inner GPKG layer name (multi-layer vector).</summary>
    public string? InnerLayerName { get; set; }
    /// <summary>Bounding box in EPSG:4326 lon/lat ([minLon, minLat, maxLon, maxLat]).</summary>
    public double[]? BboxWgs84 { get; set; }
    /// <summary>Supported CRSs (always includes EPSG:4326 and EPSG:3857).</summary>
    public string[] SupportedCrs { get; set; } = ["EPSG:4326", "EPSG:3857", "CRS:84"];
    /// <summary>Vector-only: OGR geometry type name (Point/LineString/Polygon/...).</summary>
    public string? GeometryType { get; set; }

    /// <summary>Raster-only: number of bands reported by the indexed entry (0 when unknown).</summary>
    public int BandCount { get; set; }

    /// <summary>
    /// True when the dataset build has produced the artifact required to serve this layer
    /// (COG for rasters, GPKG sidecar for vectors). Layers without a built artifact are
    /// hidden from WFS/WMTS/OGC API capabilities and surface InvalidParameterValue when
    /// referenced by name.
    /// </summary>
    public bool HasBuiltArtifact { get; set; } = true;

    /// <summary>
    /// True for a CAD drawing without a coordinate system (DDB-12): its GPKG keeps the
    /// drawing coordinates and has no SRS. Such layers are never advertised by OGC
    /// services (they cannot be served in any CRS); only OGC API Features resolves them,
    /// for the Hub's local (no globe) viewer. <see cref="BboxWgs84"/> is null for them.
    /// </summary>
    public bool IsLocal { get; set; }
}

public class OgcBoundingBoxDto
{
    public string Crs { get; set; } = "EPSG:4326";
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

public class OgcServiceMetadataDto
{
    public string Title { get; set; } = "DroneDB OGC Service";
    public string Abstract { get; set; } = "OGC services exposed by DroneDB Registry";
    public string ContactName { get; set; } = "DroneDB";
    public string ContactEmail { get; set; } = "info@dronedb.app";
    public List<string> Keywords { get; set; } = ["OGC", "DroneDB"];
}
