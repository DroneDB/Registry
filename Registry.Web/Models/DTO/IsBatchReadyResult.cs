using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Whether a share batch can take an upload for the caller, and the paths it already holds: the facts of the batch
/// the callers read, not the tracked entity (RQ-14).
/// </summary>
/// <param name="IsReady">The batch exists, is running and belongs to the caller (or the caller is an administrator).</param>
/// <param name="EntryPaths">The paths of the batch's entries (empty when it is not ready).</param>
public sealed record IsBatchReadyResult(bool IsReady, IReadOnlySet<string> EntryPaths)
{
    public static readonly IsBatchReadyResult NotReady = new(false, new HashSet<string>());
}
