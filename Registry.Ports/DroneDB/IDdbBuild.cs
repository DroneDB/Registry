using System.Collections.Generic;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Build orchestration: build/rebuild artifacts, the build manifest (buildability, build state and
/// artifacts) and cache cleanup.
/// </summary>
public interface IDdbBuild
{
    /// <summary>
    /// The folders of the database that hold what libddb derives from the files (the builds): left out of a copy
    /// of the database, which they would only make larger (Registry never names them).
    /// </summary>
    IReadOnlyList<string> DerivedFolderPaths { get; }

    void Build(string path, string dest = null, bool force = false);

    /// <summary>
    /// <see cref="Build"/> of <paramref name="path"/> under <paramref name="options"/> (the background rebuild caps
    /// its threads so it leaves the host to user builds).
    /// </summary>
    void BuildWithOptions(string path, BuildOptions options, bool force = false);

    void BuildPending(string dest = null, bool force = false);

    string GetTmpFolder(string path);

    /// <summary>
    /// Whether the dataset has pending (deferred) builds: a dataset-wide question the build
    /// manifest (<see cref="GetBuildArtifacts"/>, per entry) does not answer.
    /// </summary>
    bool IsBuildPending();

    /// <summary>
    /// The build objects of several entries with one native call (<c>DDBGetBuildArtifacts</c>): whether each is buildable, the state of its build and the artifacts that exist, one
    /// element per path, in input order. libddb owns the build layout, the states and the type
    /// rules; Registry reads them here and never composes a build path.
    /// </summary>
    /// <param name="paths">Dataset-relative entry paths (exact match).</param>
    IReadOnlyList<EntryBuildArtifacts> GetBuildArtifacts(IReadOnlyList<string> paths);

    /// <summary>
    /// A file of a build (<c>DDBResolveBuildFile</c>): an artifact or a file below a directory artifact,
    /// containment-checked by libddb, with its absolute path and the manifest's media type when libddb gives one.
    /// </summary>
    /// <param name="hash">The hash that names the build (<see cref="EntryBuild.Hash"/>).</param>
    /// <param name="path">The file, relative to the build root.</param>
    /// <returns>The file, or null when no build has this hash or the file is not one of its files.</returns>
    /// <exception cref="DdbInvalidArgumentsException">The hash is malformed or the path breaks the path rules.</exception>
    ResolvedBuildFile? ResolveBuildFile(string hash, string path);

    /// <summary>
    /// Copies the build named by <paramref name="hash"/> (its artifacts and its pending marker) to
    /// another dataset (<c>DDBCopyBuildArtifacts</c>), so a transferred file needs no rebuild.
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
