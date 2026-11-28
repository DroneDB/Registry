using System;
using System.Text.Json;
using Registry.Web.Exceptions;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The shape of the geometry of a coverage request, checked before libddb sees it: a GeoJSON Point, LineString or
/// Polygon whose coordinates nest as its type says, with a bounded number of positions. What the positions mean (the
/// range of longitude and latitude, a polygon that crosses itself) is libddb's rule alone and reaches the client as the
/// 400 of its refusal.
/// </summary>
public static class CoverageGeometry
{
    /// <summary>The most positions a geometry can have: the size of the work a request can ask for.</summary>
    public const int MaxVertices = 10_000;

    private const string Field = "geometry";

    /// <summary>The number of positions of <paramref name="geometry"/>, after checking its shape.</summary>
    /// <exception cref="InvalidFieldException">It is not a Point, LineString or Polygon, its coordinates do not nest as
    /// its type says, or it has more than <see cref="MaxVertices"/> positions.</exception>
    public static int CountVertices(JsonElement geometry)
    {
        if (geometry.ValueKind != JsonValueKind.Object)
            throw Refuse("geometry must be a GeoJSON geometry object");

        if (!geometry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw Refuse("geometry must have a type");

        var name = type.GetString();
        if (name is not ("Point" or "LineString" or "Polygon"))
            throw Refuse($"geometry type '{name}' is not supported: send a Point, a LineString or a Polygon");

        if (!geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            throw Refuse("geometry must have coordinates");

        var vertices = name switch
        {
            "Point" => Position(coordinates),
            "LineString" => Positions(coordinates, 2),
            _ => Rings(coordinates)
        };

        return vertices <= MaxVertices
            ? vertices
            : throw Refuse($"geometry can have at most {MaxVertices} positions, it has {vertices}");
    }

    // A position: two or more numbers (longitude, latitude, optional height)
    private static int Position(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
            throw Refuse("a position must be an array of at least two numbers");

        foreach (var number in position.EnumerateArray())
            if (number.ValueKind != JsonValueKind.Number)
                throw Refuse("a position must be an array of numbers");

        return 1;
    }

    private static int Positions(JsonElement list, int minimum)
    {
        if (list.GetArrayLength() < minimum)
            throw Refuse($"a position list of a geometry needs at least {minimum} positions");

        var total = 0;
        foreach (var position in list.EnumerateArray())
            total += Position(position);

        return total;
    }

    private static int Rings(JsonElement rings)
    {
        if (rings.GetArrayLength() == 0)
            throw Refuse("a Polygon needs an outer ring");

        var total = 0;
        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array)
                throw Refuse("a ring of a Polygon must be an array of positions");

            total += Positions(ring, 4);
            if (total > MaxVertices)
                break;
        }

        return total;
    }

    private static InvalidFieldException Refuse(string message) => new(Field, message);
}
