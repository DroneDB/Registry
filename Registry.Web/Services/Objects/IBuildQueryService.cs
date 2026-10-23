#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The builds of a dataset's entries: starting one, the files and downloadable artifacts libddb lists for
/// a build (XI-04), the builds waiting for a dependency, and the record of finished build jobs.
/// </summary>
public interface IBuildQueryService
{
    /// <summary>
    /// Queues a build of <paramref name="path"/> (nothing when it is not buildable); <paramref name="force"/>
    /// builds again even while a build lock is held (a stale lock of a crashed build).
    /// </summary>
    Task BuildAsync(string orgSlug, string dsSlug, string? path, bool force = false, CancellationToken ct = default);

    /// <summary>The local path of the file <paramref name="path"/> of the build <paramref name="hash"/>.</summary>
    /// <exception cref="Exceptions.NotFoundException">The file is not part of that build.</exception>
    Task<string> GetBuildFileAsync(string orgSlug, string dsSlug, string hash, string? path,
        CancellationToken ct = default);

    /// <summary>
    /// The local path of the file <paramref name="path"/> of the build <paramref name="hash"/>, or null when it is
    /// not a file of that build (a malformed hash names no build): <see cref="GetBuildFileAsync"/> without the 404.
    /// </summary>
    Task<string?> FindBuildFileAsync(string orgSlug, string dsSlug, string hash, string? path,
        CancellationToken ct = default);

    /// <summary>The downloadable artifact of the entry at <paramref name="entryPath"/> (COG, COPC, GeoPackage).</summary>
    /// <exception cref="Exceptions.InvalidDatasetPathException">The path is not a dataset path (no path: empty).</exception>
    /// <exception cref="Exceptions.BadRequestException">The entry has no downloadable artifact.</exception>
    /// <exception cref="Exceptions.NotFoundException">No such entry, or it is not built yet.</exception>
    Task<BuildDownload> GetBuildArtifactAsync(string orgSlug, string dsSlug, string? entryPath,
        CancellationToken ct = default);

    /// <summary>The builds deferred for a missing dependency (companion file or external tool).</summary>
    Task<IEnumerable<PendingBuildInfoDto>> GetPendingBuildsAsync(string orgSlug, string dsSlug,
        CancellationToken ct = default);

    /// <summary>Deletes the record of the build jobs that succeeded or failed; returns how many.</summary>
    Task<int> ClearCompletedBuildsAsync(string orgSlug, string dsSlug, CancellationToken ct = default);
}
