using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>Changing the entries of one dataset: folders, moves and deletes.</summary>
public interface IEntryCommandService
{
    /// <summary>Creates and indexes the empty folder <paramref name="path"/>.</summary>
    Task<EntryDto> CreateFolderAsync(string orgSlug, string dsSlug, string? path, CancellationToken ct = default);

    /// <summary>
    /// Moves or renames <paramref name="sourcePath"/> to <paramref name="destPath"/>; a file whose extension changed is
    /// built again when it is buildable.
    /// </summary>
    Task<EntryDto> MoveAsync(string orgSlug, string dsSlug, string? sourcePath, string? destPath, CancellationToken ct = default);

    /// <summary>
    /// Deletes every path (all or nothing: an unknown path fails the request before anything is deleted); the
    /// main file of a removed .prj sidecar is built again.
    /// </summary>
    Task DeleteAsync(string orgSlug, string dsSlug, string[] paths, CancellationToken ct = default);

    /// <summary>Deletes each path on its own and reports which were deleted and why the others were not.</summary>
    Task<DeleteBatchResponse> DeleteBatchAsync(string orgSlug, string dsSlug, string[] paths,
        CancellationToken ct = default);
}
