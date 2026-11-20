using System;
using System.Text.Json;
using Registry.Ports.Json;

namespace Registry.Web.Utilities;

/// <summary>
/// The WGS84 bounding box of GeoJSON geometries (as libddb indexes them): any geometry or a Feature of one,
/// alone or as the union of many. A coordinate that is not a finite longitude and latitude is skipped, so a
/// damaged geometry never widens a box to the whole world.
/// </summary>
public sealed class GeoJsonBbox
{
    // MultiPolygon is the deepest: polygons, rings, positions, then the numbers
    private const int MaxDepth = 6;

    private double _minLon = double.MaxValue;
    private double _minLat = double.MaxValue;
    private double _maxLon = double.MinValue;
    private double _maxLat = double.MinValue;

    /// <summary>
    /// The bounding box <c>[minLon, minLat, maxLon, maxLat]</c> of one geometry (Point, MultiPoint, LineString,
    /// Polygon, MultiPolygon or a Feature of one), or null when it has no valid coordinate.
    /// </summary>
    public static double[]? Of(JsonElement? geometry)
    {
        var bbox = new GeoJsonBbox();
        bbox.Add(geometry);
        return bbox.Result;
    }

    /// <summary>Whether <paramref name="bbox"/> is a box of the world: four finite values in range, minimums not above maximums.</summary>
    public static bool IsWithinWorld(double[]? bbox) =>
        bbox is { Length: 4 } &&
        IsLongitude(bbox[0]) && IsLatitude(bbox[1]) && IsLongitude(bbox[2]) && IsLatitude(bbox[3]) &&
        bbox[0] <= bbox[2] && bbox[1] <= bbox[3];

    /// <summary>The box of everything added so far (<c>[minLon, minLat, maxLon, maxLat]</c>), null when nothing valid was.</summary>
    public double[]? Result => _minLon > _maxLon ? null : [_minLon, _minLat, _maxLon, _maxLat];

    /// <summary>Widens the box to a geometry (a null or unreadable one adds nothing).</summary>
    public void Add(JsonElement? geometry)
    {
        if (geometry is not { } element)
            return;

        // A Feature carries its geometry
        if (element.Member("type").Text() is { } type && string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase))
            element = element.Member("geometry") ?? element;

        if (element.Member("coordinates") is { } coordinates)
            AddCoordinates(coordinates, 0);
    }

    /// <summary>Widens the box to a longitude and latitude (nothing when either is not valid).</summary>
    public void Add(double lon, double lat)
    {
        if (!IsLongitude(lon) || !IsLatitude(lat))
            return;

        _minLon = Math.Min(_minLon, lon);
        _minLat = Math.Min(_minLat, lat);
        _maxLon = Math.Max(_maxLon, lon);
        _maxLat = Math.Max(_maxLat, lat);
    }

    // The coordinates of every geometry type are positions ([lon, lat] or [lon, lat, z]) in arrays nested as deep as
    // the type needs, so one walk reads them all
    private void AddCoordinates(JsonElement coordinates, int depth)
    {
        if (coordinates.ValueKind != JsonValueKind.Array || depth > MaxDepth)
            return;

        var first = coordinates.GetArrayLength() > 0 ? coordinates[0] : default;
        if (first.ValueKind == JsonValueKind.Number)
        {
            // A number out of the range of a double is not a coordinate
            if (coordinates.GetArrayLength() >= 2 && first.TryGetDouble(out var lon) &&
                coordinates[1].ValueKind == JsonValueKind.Number && coordinates[1].TryGetDouble(out var lat))
                Add(lon, lat);
            return;
        }

        foreach (var child in coordinates.EnumerateArray())
            AddCoordinates(child, depth + 1);
    }

    private static bool IsLongitude(double value) => double.IsFinite(value) && Math.Abs(value) <= 180;

    private static bool IsLatitude(double value) => double.IsFinite(value) && Math.Abs(value) <= 90;
}
