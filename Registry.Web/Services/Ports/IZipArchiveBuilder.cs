#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Single source of truth for building a ZIP archive out of dataset entries.
/// Shared by the legacy download streaming path (<c>FileStreamDescriptor</c>)
/// and the asynchronous <c>bulk-download</c> heavy tool (spec §A.0).
/// Stateless: all dataset state is passed in via <see cref="IDDB"/>, so the
/// implementation is registered as a singleton.
/// </summary>
public interface IZipArchiveBuilder
{
    /// <summary>
    /// Expands the requested paths into explicit files, folders (including empty
    /// ones) and the <c>includeDdb</c> flag (set when the whole dataset is requested,
    /// i.e. <paramref name="paths"/> is null/empty). Mirrors the legacy
    /// <c>ObjectsManager.GetFilePaths</c> behaviour.
    /// </summary>
    (string[] files, string[] folders, bool includeDdb) ExpandPaths(IDDB ddb, string[]? paths);

    /// <summary>
    /// Writes a ZIP archive to <paramref name="output"/>. Files are added with the
    /// per-extension compression level; empty folders are added as directory entries;
    /// when <paramref name="includeDdb"/> is true the dataset <c>.ddb</c> database
    /// folder is embedded (excluding the build folder), reproducing the legacy
    /// whole-dataset archive exactly.
    /// </summary>
    /// <param name="progress">Reports cumulative bytes written, for progress mapping.</param>
    Task WriteZipAsync(IDDB ddb, string[] files, string[] folders, bool includeDdb,
        Stream output, IProgress<long>? progress = null, CancellationToken ct = default);
}
