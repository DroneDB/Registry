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

    /// <summary>Retries the builds that wait for a dependency.</summary>
    Task BuildPending();
}
