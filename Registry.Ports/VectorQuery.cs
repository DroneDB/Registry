using System.Collections.Generic;

namespace Registry.Ports;

/// <summary>
/// Which features of a vector dataset <see cref="IDdbWrapper.QueryVector"/> returns and
/// <see cref="IDdbWrapper.CountVector"/> counts (WFS GetFeature, OGC API Features items).
/// </summary>
/// <param name="LayerName">Layer to query; null uses the first layer.</param>
/// <param name="Bbox">[minX,minY,maxX,maxY] spatial filter in <paramref name="BboxSrs"/>, or null.</param>
/// <param name="BboxSrs">CRS of <paramref name="Bbox"/> (e.g. "EPSG:4326"); null when there is no bbox.</param>
/// <param name="Fids">Feature ids to return (read by primary key from a GeoPackage), or null for every feature.
/// Feature ids are stable: GML ids are "&lt;layer&gt;.&lt;fid&gt;", GeoJSON features carry "id": fid.</param>
/// <param name="MaxFeatures">Maximum features to return (clamped to [1,10000]; 0 → default 1000). Not used by counts.</param>
/// <param name="StartIndex">0-based feature offset for pagination. Not used by counts.</param>
/// <param name="OutputFormat">"application/json" (RFC7946 GeoJSON, default) or "application/gml+xml". Not used by counts.</param>
/// <param name="Where">Attribute filter, or null: an SQL expression over the layer's fields (SQLite syntax for a
/// GeoPackage) applied as given. Build it from quoted identifiers of existing fields and quoted literals only
/// (SQLite reads an unknown double-quoted identifier as a string).</param>
public sealed record VectorQuery(
    string? LayerName = null,
    double[]? Bbox = null,
    string? BboxSrs = null,
    IReadOnlyList<long>? Fids = null,
    int MaxFeatures = 1000,
    int StartIndex = 0,
    string OutputFormat = "application/json",
    string? Where = null);
