using System.Collections.Generic;
using Newtonsoft.Json;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.DTO.Ogc;

/// <summary>Single feature-class / coverage / layer exposed via the OGC suite.</summary>
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
    public string[] SupportedCrs { get; set; } = { "EPSG:4326", "EPSG:3857", "CRS:84" };
    /// <summary>Vector-only: OGR geometry type name (Point/LineString/Polygon/...).</summary>
    public string? GeometryType { get; set; }
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
    public List<string> Keywords { get; set; } = new() { "OGC", "DroneDB" };
}
