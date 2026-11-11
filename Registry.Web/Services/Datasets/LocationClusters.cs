using System;
using System.Collections.Generic;
using System.Linq;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// The representative point of a dataset whose data is in several places (FR-ORG-09): the centre of its box is no place
/// of its data when the data lies on two continents (it sits in the ocean between them). The point is a position of the
/// data, in the densest cluster of it.
/// </summary>
public static class LocationClusters
{
    /// <summary>The side of a cell of the grid the positions are counted in, in degrees (about 110 km at the equator).</summary>
    public const double CellDegrees = 1.0;

    /// <summary>
    /// A box no wider and no higher than this many degrees is one place: its centre is where the data is, and no
    /// representative point is given (the dataset of a single survey is not read position by position).
    /// </summary>
    public const double CompactDegrees = 1.0;

    /// <summary>Whether the box <c>[minLon, minLat, maxLon, maxLat]</c> is one place (see <see cref="CompactDegrees"/>).</summary>
    public static bool IsCompact(double[] bbox) =>
        bbox[2] - bbox[0] <= CompactDegrees && bbox[3] - bbox[1] <= CompactDegrees;

    /// <summary>
    /// The position of <paramref name="positions"/> to pin the dataset at, <c>[lon, lat]</c>: the grid cell with the most
    /// positions in it and in the cells around it is the densest cluster (the first cell, south to north then west to
    /// east, when two are equally dense), and the position of that cluster nearest to its centroid is returned, so the
    /// point is a place where the dataset has data. Null when there is no position.
    /// </summary>
    public static double[]? RepresentativePoint(IReadOnlyCollection<(double Lon, double Lat)> positions)
    {
        if (positions.Count == 0)
            return null;

        var cells = new Dictionary<(int Lat, int Lon), int>();
        foreach (var (lon, lat) in positions)
        {
            var cell = CellOf(lon, lat);
            cells[cell] = cells.GetValueOrDefault(cell) + 1;
        }

        var best = cells.Keys
            .Select(cell => (Cell: cell, Count: DensityAround(cells, cell)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Cell.Lat).ThenBy(c => c.Cell.Lon)
            .First().Cell;

        var cluster = positions.Where(p =>
        {
            var cell = CellOf(p.Lon, p.Lat);
            return Math.Abs(cell.Lat - best.Lat) <= 1 && Math.Abs(cell.Lon - best.Lon) <= 1;
        }).ToList();

        var centreLon = cluster.Average(p => p.Lon);
        var centreLat = cluster.Average(p => p.Lat);
        var nearest = cluster
            .OrderBy(p => (p.Lon - centreLon) * (p.Lon - centreLon) + (p.Lat - centreLat) * (p.Lat - centreLat))
            .ThenBy(p => p.Lat).ThenBy(p => p.Lon)
            .First();
        return [nearest.Lon, nearest.Lat];
    }

    private static (int Lat, int Lon) CellOf(double lon, double lat) =>
        ((int)Math.Floor(lat / CellDegrees), (int)Math.Floor(lon / CellDegrees));

    // The positions in the cell and the eight around it
    private static int DensityAround(Dictionary<(int Lat, int Lon), int> cells, (int Lat, int Lon) cell)
    {
        var count = 0;
        for (var dLat = -1; dLat <= 1; dLat++)
        for (var dLon = -1; dLon <= 1; dLon++)
            count += cells.GetValueOrDefault((cell.Lat + dLat, cell.Lon + dLon));
        return count;
    }
}
