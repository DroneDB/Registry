using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Specifies how to handle conflicts when moving datasets.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>
    /// Stop the operation if a conflict is detected.
    /// </summary>
    HaltOnConflict = 0,

    /// <summary>
    /// Overwrite the existing dataset in the destination.
    /// </summary>
    Overwrite = 1,

    /// <summary>
    /// Rename the dataset being moved to avoid conflicts.
    /// </summary>
    Rename = 2
}

/// <summary>
/// DTO for moving one or more datasets to a different organization.
/// </summary>
public class MoveDatasetDto
{
    /// <summary>
    /// The slug of the source organization.
    /// </summary>
    public string SourceOrgSlug { get; set; }
    
    /// <summary>
    /// The slugs of the datasets to move.
    /// </summary>
    [Required]
    public string[] DatasetSlugs { get; set; }

    /// <summary>
    /// The slug of the destination organization.
    /// </summary>
    [Required]
    public string DestinationOrgSlug { get; set; }

    /// <summary>
    /// How to handle conflicts when a dataset with the same slug already exists in the destination.
    /// </summary>
    public ConflictResolutionStrategy ConflictResolution { get; set; } = ConflictResolutionStrategy.HaltOnConflict;
}

/// <summary>
/// Result of a dataset move operation.
/// </summary>
public class MoveDatasetResultDto
{
    /// <summary>
    /// The original slug of the dataset.
    /// </summary>
    public string OriginalSlug { get; set; }

    /// <summary>
    /// The new slug of the dataset (may differ if renamed).
    /// </summary>
    public string NewSlug { get; set; }

    /// <summary>
    /// Whether the move was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the move failed.
    /// </summary>
    public string Error { get; set; }
}
