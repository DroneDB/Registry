using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// DTO for merging two organizations into one.
/// All datasets from the source organization will be moved to the destination organization.
/// </summary>
public class MergeOrganizationDto
{
    
    /// <summary>
    /// The slug of the source organization from which all datasets will be moved.
    /// </summary>
    public string SourceOrgSlug { get; set; }
    
    /// <summary>
    /// The slug of the destination organization where all datasets will be moved.
    /// </summary>
    [Required]
    public string DestinationOrgSlug { get; set; }

    /// <summary>
    /// How to handle conflicts when a dataset with the same slug already exists in the destination.
    /// </summary>
    public ConflictResolutionStrategy ConflictResolution { get; set; } = ConflictResolutionStrategy.HaltOnConflict;

    /// <summary>
    /// Whether to delete the source organization after merging.
    /// Default is true.
    /// </summary>
    public bool DeleteSourceOrganization { get; set; } = true;
}

/// <summary>
/// Result of an organization merge operation.
/// </summary>
public class MergeOrganizationResultDto
{
    /// <summary>
    /// The slug of the source organization that was merged.
    /// </summary>
    public string SourceOrgSlug { get; set; }

    /// <summary>
    /// The slug of the destination organization.
    /// </summary>
    public string DestinationOrgSlug { get; set; }

    /// <summary>
    /// Results for each dataset that was moved.
    /// </summary>
    public MoveDatasetResultDto[] DatasetResults { get; set; }

    /// <summary>
    /// Whether the source organization was deleted.
    /// </summary>
    public bool SourceOrganizationDeleted { get; set; }

    /// <summary>
    /// Total number of datasets successfully moved.
    /// </summary>
    public int DatasetsMovedCount { get; set; }

    /// <summary>
    /// Total number of datasets that failed to move.
    /// </summary>
    public int DatasetsFailedCount { get; set; }
}
