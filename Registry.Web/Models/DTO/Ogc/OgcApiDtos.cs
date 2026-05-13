using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO.Ogc;

/// <summary>
/// MVT TileJSON 3.0 metadata (frontend consumer + OGC API Tiles).
/// </summary>
public class MvtMetadataDto
{
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("format")] public string Format { get; set; } = "pbf";
    [JsonProperty("minzoom")] public int MinZoom { get; set; }
    [JsonProperty("maxzoom")] public int MaxZoom { get; set; } = 18;
    [JsonProperty("bounds")] public double[]? Bounds { get; set; }
    [JsonProperty("center")] public double[]? Center { get; set; }
    [JsonProperty("vector_layers")] public List<MvtVectorLayerDto> VectorLayers { get; set; } = new();
}

public class MvtVectorLayerDto
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("fields")] public Dictionary<string, string> Fields { get; set; } = new();
    [JsonProperty("minzoom")] public int MinZoom { get; set; }
    [JsonProperty("maxzoom")] public int MaxZoom { get; set; } = 18;
}

/// <summary>OGC API – Features: landing page links.</summary>
public class OgcApiLandingDto
{
    [JsonProperty("title")] public string Title { get; set; } = "DroneDB OGC API";
    [JsonProperty("description")] public string Description { get; set; } = "OGC API – Features + Tiles exposed by DroneDB Registry";
    [JsonProperty("links")] public List<OgcApiLinkDto> Links { get; set; } = new();
}

public class OgcApiLinkDto
{
    [JsonProperty("href")] public string Href { get; set; } = string.Empty;
    [JsonProperty("rel")] public string Rel { get; set; } = "self";
    [JsonProperty("type")] public string Type { get; set; } = "application/json";
    [JsonProperty("title")] public string? Title { get; set; }
}

public class OgcApiCollectionsDto
{
    [JsonProperty("links")] public List<OgcApiLinkDto> Links { get; set; } = new();
    [JsonProperty("collections")] public List<OgcApiCollectionDto> Collections { get; set; } = new();
}

public class OgcApiCollectionDto
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("itemType")] public string ItemType { get; set; } = "feature";
    [JsonProperty("crs")] public List<string> Crs { get; set; } = new() { "http://www.opengis.net/def/crs/OGC/1.3/CRS84" };
    [JsonProperty("extent")] public OgcApiExtentDto? Extent { get; set; }
    [JsonProperty("links")] public List<OgcApiLinkDto> Links { get; set; } = new();
}

public class OgcApiExtentDto
{
    [JsonProperty("spatial")] public OgcApiSpatialExtentDto Spatial { get; set; } = new();
}

public class OgcApiSpatialExtentDto
{
    [JsonProperty("bbox")] public double[][] Bbox { get; set; } = new[] { new[] { -180.0, -90.0, 180.0, 90.0 } };
    [JsonProperty("crs")] public string Crs { get; set; } = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}

public class OgcApiTileSetDto
{
    [JsonProperty("tileMatrixSetURI")] public string TileMatrixSetUri { get; set; } =
        "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad";
    [JsonProperty("links")] public List<OgcApiLinkDto> Links { get; set; } = new();
}

public class OgcConformanceDto
{
    [JsonProperty("conformsTo")] public List<string> ConformsTo { get; set; } = new()
    {
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
        "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/geodata-tilesets"
    };
}
