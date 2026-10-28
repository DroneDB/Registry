#nullable enable
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// How the rebuilds after a rescan run: inline (inside a task that already runs on the
/// builds queue) or queued as build jobs (from a request). Every build is forced.
/// </summary>
public interface IRescanBuildRunner
{
    /// <summary>Rebuilds every entry of the dataset.</summary>
    Task BuildDataset();

    /// <summary>Rebuilds one rescanned entry.</summary>
    Task BuildEntry(RescanResult entry);

    /// <summary>Retries the pending builds a retry can progress (not those that wait for missing files).</summary>
    Task BuildPending();
}
