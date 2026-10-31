using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>A dataset of an organization on the map: where it is, nothing else.</summary>
public class DatasetLocationDto
{
    [Required]
    public required string Slug { get; set; }

    /// <summary>The display name of the dataset, null when it has none.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The bounding box in WGS84, <c>[minLon, minLat, maxLon, maxLat]</c>: null when the dataset has no location
    /// (no geo-referenced entry, or an index that cannot be read). A dataset with a single position has a box of
    /// zero size.
    /// </summary>
    public double[]? Bbox { get; set; }
}
