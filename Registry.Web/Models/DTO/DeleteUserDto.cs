using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Models.DTO;

/// <summary>
/// DTO for deleting a user with optional data transfer to a successor.
/// </summary>
public class DeleteUserDto
{
    /// <summary>
    /// The username of the user to delete.
    /// </summary>
    [Required]
    public string UserName { get; set; }

    /// <summary>
    /// The username of the successor to transfer data to.
    /// If null, all user data (organizations, datasets, batches) will be deleted.
    /// </summary>
    public string Successor { get; set; }

    /// <summary>
    /// How to handle conflicts when transferring datasets to the successor's organizations.
    /// Only used when Successor is specified.
    /// </summary>
    public ConflictResolutionStrategy ConflictResolution { get; set; } = ConflictResolutionStrategy.Rename;
}

/// <summary>
/// Result of a user deletion operation.
/// </summary>
public class DeleteUserResultDto
{
    /// <summary>
    /// The username of the deleted user.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// The username of the successor (if any).
    /// </summary>
    public string Successor { get; set; }

    /// <summary>
    /// Number of organizations transferred to the successor.
    /// </summary>
    public int OrganizationsTransferred { get; set; }

    /// <summary>
    /// Number of organizations deleted.
    /// </summary>
    public int OrganizationsDeleted { get; set; }

    /// <summary>
    /// Number of datasets transferred to the successor.
    /// </summary>
    public int DatasetsTransferred { get; set; }

    /// <summary>
    /// Number of datasets deleted.
    /// </summary>
    public int DatasetsDeleted { get; set; }

    /// <summary>
    /// Number of batches deleted.
    /// </summary>
    public int BatchesDeleted { get; set; }

    /// <summary>
    /// Details of dataset move operations (when transferring to successor).
    /// </summary>
    public MoveDatasetResultDto[] DatasetResults { get; set; }
}
