#nullable enable
using Hangfire.Server;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Builds run inline, in the calling job (a heavy task that already holds a builds-queue worker),
/// with the same logging and failure handling as the build jobs (<see cref="DdbJobs"/>).
/// </summary>
public interface IDatasetBuilds
{
    /// <summary>Builds <paramref name="path"/> of <paramref name="ddb"/> (null: the whole dataset).</summary>
    void BuildNow(IDDB ddb, string? path, bool force, PerformContext? context);

    /// <summary>The pending-build work of <paramref name="ddb"/> (<see cref="PendingBuildEnqueuer"/>).</summary>
    void BuildPendingNow(IDDB ddb, PerformContext? context);
}
