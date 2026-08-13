namespace Registry.Ports.DroneDB;

/// <summary>
/// Build orchestration: build/rebuild artifacts, buildability/build-state queries and cache
/// cleanup. See ImproveParallelWrites plan, workstream 04 §7.
/// </summary>
public interface IDdbBuild
{
    /// <summary>
    /// The build folder path
    /// </summary>
    string BuildFolderPath { get; }

    void Build(string path, string dest = null, bool force = false);
    void BuildAll(string dest = null, bool force = false);
    void BuildPending(string dest = null, bool force = false);

    string GetTmpFolder(string path);
    bool IsBuildable(string path);
    bool IsBuildActive(string path);
    bool IsBuildComplete(string path);
    bool IsBuildPending();

    /// <summary>
    /// Gets information about pending (deferred) builds in this dataset, i.e.
    /// builds skipped because one or more dependencies were missing at the
    /// time of the attempt. Read-only: does not consume pending markers or
    /// trigger builds.
    /// </summary>
    PendingBuildInfo[] GetPendingBuildInfo();

    /// <summary>
    /// Cleans up the dataset by removing index entries whose underlying files no
    /// longer exist on disk and orphaned build artifacts.
    /// </summary>
    DdbCleanupResult Cleanup();

    /// <summary>
    /// Clears the build cache (thumbnails, tiles, COGs, etc.) for the dataset
    /// </summary>
    void ClearBuildCache();
}
