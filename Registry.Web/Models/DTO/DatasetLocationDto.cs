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
    /// The tagline of the dataset, null when it has none: what the search of the datasets also matches (name, slug and
    /// tagline), so the map is filtered by the same text as the list (FR-ORG-10).
    /// </summary>
    public string? Tagline { get; set; }

    /// <summary>
    /// The bounding box in WGS84, <c>[minLon, minLat, maxLon, maxLat]</c>: null when the dataset has no location
    /// (no geo-referenced entry, or an index that cannot be read). A dataset with a single position has a box of
    /// zero size.
    /// </summary>
    public double[]? Bbox { get; set; }

    /// <summary>
    /// Where to pin the dataset, <c>[lon, lat]</c> in WGS84: a position of its data in the densest cluster of it, so a
    /// dataset with data in several places is not pinned in the sea between them. Null when the box is one place
    /// (pin its centre) or when there is no <see cref="Bbox"/>.
    /// </summary>
    public double[]? Point { get; set; }
}
