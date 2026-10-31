using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Whether an entry may be written at a destination by copying it there (a transfer, a copy): the current
/// user's storage quota, then the extension policy (files only: a folder has no extension and would be
/// refused outright in allow-list mode).
/// </summary>
public sealed class EntryWritePolicy
{
    private readonly IStorageQuota _quota;
    private readonly ImportSettings _import;

    public EntryWritePolicy(IStorageQuota quota, IOptions<ImportSettings> import)
    {
        _quota = quota;
        _import = import.Value;
    }

    /// <exception cref="QuotaExceededException">The user has not <paramref name="bytes"/> of storage left.</exception>
    /// <exception cref="ExtensionBlockedException">The destination file's extension is not allowed.</exception>
    public async Task EnsureAllowedAsync(string destinationPath, bool isFolder, long bytes)
    {
        await _quota.CheckCurrentUserStorage(bytes);

        if (!isFolder && !_import.IsExtensionAllowed(destinationPath))
            throw new ExtensionBlockedException(destinationPath, _import.AllowedFileExtensions.Length > 0);
    }
}
