#nullable disable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO.Ogc;

/// <summary>
/// MVT TileJSON 3.0 metadata (frontend consumer + OGC API Tiles).
/// </summary>
public class MvtMetadataDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = "pbf";
    [JsonPropertyName("minzoom")] public int MinZoom { get; set; }
    [JsonPropertyName("maxzoom")] public int MaxZoom { get; set; } = 18;
    [JsonPropertyName("bounds")] public double[]? Bounds { get; set; }
    [JsonPropertyName("center")] public double[]? Center { get; set; }
    [JsonPropertyName("vector_layers")] public List<MvtVectorLayerDto> VectorLayers { get; set; } = [];
}

public class MvtVectorLayerDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("fields")] public Dictionary<string, string> Fields { get; set; } = new();
    [JsonPropertyName("minzoom")] public int MinZoom { get; set; }
    [JsonPropertyName("maxzoom")] public int MaxZoom { get; set; } = 18;
}

/// <summary>
/// OGC API – Features: landing page links.
/// </summary>
public class OgcApiLandingDto
{
    [JsonPropertyName("title")] public string Title { get; set; } = "DroneDB OGC API";
    [JsonPropertyName("description")] public string Description { get; set; } = "OGC API – Features + Tiles exposed by DroneDB Registry";
    [JsonPropertyName("links")] public List<OgcApiLinkDto> Links { get; set; } = [];
}

public class OgcApiLinkDto
{
    [JsonPropertyName("href")] public string Href { get; set; } = string.Empty;
    [JsonPropertyName("rel")] public string Rel { get; set; } = "self";
    [JsonPropertyName("type")] public string Type { get; set; } = "application/json";
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public class OgcApiCollectionsDto
{
    [JsonPropertyName("links")] public List<OgcApiLinkDto> Links { get; set; } = [];
    [JsonPropertyName("collections")] public List<OgcApiCollectionDto> Collections { get; set; } = [];
}

public class OgcApiCollectionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("itemType")] public string ItemType { get; set; } = "feature";
    [JsonPropertyName("crs")] public List<string> Crs { get; set; } = ["http://www.opengis.net/def/crs/OGC/1.3/CRS84"];
    [JsonPropertyName("extent")] public OgcApiExtentDto? Extent { get; set; }
    [JsonPropertyName("links")] public List<OgcApiLinkDto> Links { get; set; } = [];
}

public class OgcApiExtentDto
{
    [JsonPropertyName("spatial")] public OgcApiSpatialExtentDto Spatial { get; set; } = new();
}

public class OgcApiSpatialExtentDto
{
    [JsonPropertyName("bbox")] public double[][] Bbox { get; set; } = [[-180.0, -90.0, 180.0, 90.0]];
    [JsonPropertyName("crs")] public string Crs { get; set; } = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
}

/// <summary>The tile sets of a collection (OGC API Tiles).</summary>
public sealed record OgcApiTileSetsDto(List<OgcApiTileSetDto> Tilesets);

/// <summary>A tile set: its tile matrix set and the link template of its tiles.</summary>
public sealed record OgcApiTileSetDto(
    [property: JsonPropertyName("tileMatrixSetURI")] string TileMatrixSetUri,
    List<OgcApiTileLinkDto> Links);

/// <summary>A link of a tile set (no title, unlike <see cref="OgcApiLinkDto"/>).</summary>
public sealed record OgcApiTileLinkDto(string Href, string Rel, string Type);

public class OgcConformanceDto
{
    [JsonPropertyName("conformsTo")] public List<string> ConformsTo { get; set; } =
    [
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
        "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/geodata-tilesets"
    ];
}
