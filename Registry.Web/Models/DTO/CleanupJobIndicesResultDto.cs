namespace Registry.Web.Models.DTO;

/// <summary>
/// Result of a JobIndex cleanup operation.
/// </summary>
public class CleanupJobIndicesResultDto
{
    /// <summary>
    /// Number of terminal records deleted.
    /// </summary>
    public int DeletedCount { get; set; }

    /// <summary>
    /// Number of retention days used for the cutoff.
    /// </summary>
    public int RetentionDays { get; set; }
}
