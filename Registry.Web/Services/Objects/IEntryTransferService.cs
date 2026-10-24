#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>Copying entries: to another dataset (a transfer, optionally a move) or within a dataset.</summary>
public interface IEntryTransferService
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destPath"/> of another dataset (the source name in
    /// its root when null) with the builds of its files, then removes the source unless
    /// <paramref name="keepSource"/>. A failure leaves both datasets as they were.
    /// </summary>
    Task TransferAsync(string sourceOrgSlug, string sourceDsSlug, string sourcePath, string destOrgSlug,
        string destDsSlug, string? destPath = null, bool overwrite = false, bool keepSource = false,
        CancellationToken ct = default);

    /// <summary>
    /// Copies a file or folder within a dataset. The copy reuses the build of its content (builds are named by
    /// content hash); an overwritten destination is restored when the copy fails.
    /// </summary>
    Task<EntryDto> CopyAsync(string orgSlug, string dsSlug, string source, string dest, bool overwrite = false,
        CancellationToken ct = default);
}
