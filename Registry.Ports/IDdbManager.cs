using System;
using Registry.Ports.DroneDB;

namespace Registry.Ports;

/// <summary>
/// Creates new instances of DDB
/// </summary>
public interface IDdbManager
{
    IDDB Get(string orgSlug, Guid internalRef);

    /// <summary>
    /// The existing dataset <paramref name="orgSlug"/>/<paramref name="internalRef"/>. Unlike
    /// <see cref="Get"/> it never creates or initializes one: a background job for a dataset
    /// deleted in the meantime must not bring its folder back.
    /// </summary>
    /// <exception cref="System.IO.DirectoryNotFoundException">The dataset has no folder or no index.</exception>
    IDDB Open(string orgSlug, Guid internalRef);

    /// <summary>
    /// The organization and <c>InternalRef</c> of the dataset folder <paramref name="datasetFolderPath"/>
    /// (the inverse of the folder <see cref="Get"/> uses), or false when it is not a dataset folder
    /// directly under the datasets path.
    /// </summary>
    bool TryParseDatasetFolder(string? datasetFolderPath, out string orgSlug, out Guid internalRef);
    void Delete(string orgSlug, Guid internalRef);

    void Delete(string orgSlug);

    /// <summary>
    /// Closes the idle native index connections of the datasets under <paramref name="folder"/>
    /// (a dataset or organization folder). Call it before deleting, moving or replacing the
    /// folder: idle connections keep the index files open. Best effort: logs and returns when
    /// DroneDB cannot release them.
    /// </summary>
    void ReleaseConnections(string folder);
}