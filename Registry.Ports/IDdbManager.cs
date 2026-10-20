using System;
using Registry.Ports.DroneDB;

namespace Registry.Ports;

/// <summary>
/// Creates new instances of DDB
/// </summary>
public interface IDdbManager
{
    IDDB Get(string orgSlug, Guid internalRef);
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