#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.Import;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Import;

/// <summary>
/// Shared client for the remote-registry transfer operations (authenticate, list, parallel download
/// with same-hash skip). Used by the <c>registry</c> import source. Downloads only - indexing is the
/// caller's responsibility (the import tool indexes via <c>AddRawBatch</c> after a successful fetch).
/// </summary>
public interface IRemoteRegistryClient
{
    /// <summary>
    /// Authenticates against a remote registry, returning a bearer token, or null on failure.
    /// When both <paramref name="username"/> and <paramref name="password"/> are blank the remote is
    /// treated as anonymous and null is returned (no Authorization header is sent downstream).
    /// </summary>
    /// <param name="registryUrl">The remote registry base URL.</param>
    /// <param name="username">The username (may be blank for anonymous access).</param>
    /// <param name="password">The password (may be blank for anonymous access).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A bearer token, or null.</returns>
    Task<string?> AuthenticateAsync(string registryUrl, string username, string password, CancellationToken ct);

    /// <summary>
    /// Lists the files of a remote dataset (recursive), excluding directories.
    /// </summary>
    /// <param name="registryUrl">The remote registry base URL.</param>
    /// <param name="authToken">The bearer token, or null for anonymous.</param>
    /// <param name="orgSlug">The remote organization slug.</param>
    /// <param name="dsSlug">The remote dataset slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The remote file entries.</returns>
    Task<EntryDto[]> ListFilesAsync(string registryUrl, string? authToken, string orgSlug, string dsSlug,
        CancellationToken ct);

    /// <summary>
    /// Downloads the given files into <paramref name="destFolder"/> in parallel, skipping files already
    /// present on disk with the matching size (resumable re-import). Reports incremental progress.
    /// </summary>
    /// <param name="registryUrl">The remote registry base URL.</param>
    /// <param name="authToken">The bearer token, or null for anonymous.</param>
    /// <param name="sourceOrg">The remote organization slug.</param>
    /// <param name="sourceDs">The remote dataset slug.</param>
    /// <param name="destFolder">The destination folder (the dataset folder).</param>
    /// <param name="files">The files to download.</param>
    /// <param name="progress">Progress sink (may be null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when all files have been downloaded.</returns>
    Task DownloadFilesParallelAsync(string registryUrl, string? authToken, string sourceOrg, string sourceDs,
        string destFolder, EntryDto[] files, IProgress<ImportProgress>? progress, CancellationToken ct);
}
