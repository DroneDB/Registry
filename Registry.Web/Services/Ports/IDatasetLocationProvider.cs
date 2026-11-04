using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>Where a dataset is on the map.</summary>
/// <param name="Bbox">The bounding box in WGS84, <c>[minLon, minLat, maxLon, maxLat]</c>.</param>
public sealed record DatasetLocation(double[] Bbox);

/// <summary>
/// The location of a dataset, for the maps that show many datasets at once. It is read once per state of the
/// dataset's index and kept, so the cost of reading the geometry of a large dataset is not paid by every map.
/// </summary>
public interface IDatasetLocationProvider
{
    /// <summary>
    /// The location of the dataset <paramref name="orgSlug"/>/<paramref name="internalRef"/>: the extent libddb
    /// computes for it or, when that has none (a dataset of photos with a position and no footprint), the box of the
    /// positions and footprints of its geo-referenced entries. Null when it has no location or its index cannot be
    /// read (logged); an index that is only busy is an error the caller retries.
    /// </summary>
    Task<DatasetLocation?> GetAsync(string orgSlug, Guid internalRef, CancellationToken ct = default);
}
