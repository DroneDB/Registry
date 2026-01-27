using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Response for batch delete operations containing results for each path.
/// </summary>
public class DeleteBatchResponse
{
    /// <summary>
    /// List of paths that were successfully deleted.
    /// </summary>
    public string[] Deleted { get; set; } = [];

    /// <summary>
    /// Dictionary of paths that failed to delete, with error messages.
    /// Key is the path, value is the error message.
    /// </summary>
    public Dictionary<string, string> Failed { get; set; } = new();
}
