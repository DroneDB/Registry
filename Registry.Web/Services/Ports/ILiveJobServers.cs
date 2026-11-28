using System.Collections.Generic;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The job servers that are alive now (on every host that shares the job store). The store keeps the list by heartbeat,
/// so a server that crashed leaves it after its timeout: a worker it named is no longer taken as running.
/// </summary>
public interface ILiveJobServers
{
    /// <summary>The ids of the live servers (what a job's Processing state names as its server).</summary>
    IReadOnlySet<string> Ids();
}
