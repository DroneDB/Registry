#nullable enable
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// How the rebuilds after a rescan run: inline (inside a task that already runs on the
/// builds queue) or queued as build jobs (from a request). Every build is forced.
/// </summary>
public interface IRescanBuildRunner
{
    /// <summary>Rebuilds every entry of the dataset.</summary>
    void BuildDataset();

    /// <summary>Rebuilds one rescanned entry.</summary>
    void BuildEntry(RescanResult entry);

    /// <summary>Retries the builds that wait for a dependency.</summary>
    void BuildPending();
}
