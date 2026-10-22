using System.Collections.Generic;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Build orchestration: build/rebuild artifacts, the build manifest (buildability, build state and
/// artifacts, XI-04) and cache cleanup.
/// </summary>
public interface IDdbBuild
{
    /// <summary>
    /// The folders of the database that hold what libddb derives from the files (the builds): left out of a copy
    /// of the database, which they would only make larger (XI-04: Registry never names them).
    /// </summary>
    IReadOnlyList<string> DerivedFolderPaths { get; }

    void Build(string path, string dest = null, bool force = false);
    void BuildAll(string dest = null, bool force = false);
    void BuildPending(string dest = null, bool force = false);

    string GetTmpFolder(string path);

    /// <summary>
    /// Whether the dataset has pending (deferred) builds: a dataset-wide question the build
    /// manifest (<see cref="GetBuildArtifacts"/>, per entry) does not answer.
    /// </summary>
    bool IsBuildPending();

    /// <summary>
    /// The build objects of several entries with one native call (<c>DDBGetBuildArtifacts</c>,
    /// XI-04): whether each is buildable, the state of its build and the artifacts that exist, one
    /// element per path, in input order. libddb owns the build layout, the states and the type
    /// rules; Registry reads them here and never composes a build path.
    /// </summary>
    /// <param name="paths">Dataset-relative entry paths (exact match).</param>
    IReadOnlyList<EntryBuildArtifacts> GetBuildArtifacts(IReadOnlyList<string> paths);

    /// <summary>
    /// The absolute path of a file of a build (<c>DDBResolveBuildFile</c>, XI-04): an artifact or a
    /// file below a directory artifact, containment-checked by libddb.
    /// </summary>
    /// <param name="hash">The hash that names the build (<see cref="EntryBuild.Hash"/>).</param>
    /// <param name="path">The file, relative to the build root.</param>
    /// <returns>The absolute path, or null when no build has this hash or the file is not one of its files.</returns>
    /// <exception cref="DdbInvalidArgumentsException">The hash is malformed or the path breaks the path rules.</exception>
    string? ResolveBuildFile(string hash, string path);

    /// <summary>
    /// Copies the build named by <paramref name="hash"/> (its artifacts and its pending marker) to
    /// another dataset (<c>DDBCopyBuildArtifacts</c>, XI-04), so a transferred file needs no rebuild.
    /// </summary>
    /// <param name="destinationDatasetFolder">The dataset folder of the destination.</param>
    /// <param name="hash">The hash that names the build.</param>
    /// <exception cref="DdbNotFoundException">No buildable entry of this dataset has this hash.</exception>
    /// <exception cref="DdbBuildInProgressException">A build of it runs in either dataset (nothing copied).</exception>
    /// <exception cref="DdbRetryableException">The destination's disk is (nearly) full.</exception>
    CopiedBuildArtifacts CopyBuildArtifactsTo(string destinationDatasetFolder, string hash);

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
