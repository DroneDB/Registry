using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// How the rebuilds after a rescan are queued: as forced build tasks, children of the rescan task, or on the
/// background rebuild queue (the upgrade rescans). The rescan does not wait for them: a build that fails is a failed
/// build, not a failed rescan.
/// </summary>
public interface IRescanBuildRunner
{
    /// <summary>Queues the rebuild of every entry of the dataset.</summary>
    Task BuildDataset();

    /// <summary>Queues the rebuild of one rescanned entry.</summary>
    Task BuildEntry(RescanResult entry);

    /// <summary>Retries the pending builds a retry can progress (not those that wait for missing files).</summary>
    Task BuildPending();
}
