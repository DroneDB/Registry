namespace Registry.Web.Models.DTO;

/// <summary>
/// Response model for the clear completed builds operation.
/// </summary>
public class ClearCompletedBuildsResponse
{
    /// <summary>
    /// The number of deleted build jobs.
    /// </summary>
    public int DeletedCount { get; set; }
}
