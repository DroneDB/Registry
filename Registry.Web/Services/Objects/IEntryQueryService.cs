#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>Reading the entries of a dataset: listings, search, one entry, and downloads of entries.</summary>
public interface IEntryQueryService
{
    /// <summary>The entries in <paramref name="path"/> (the root when null), annotated with their build status.</summary>
    Task<IEnumerable<EntryDto>> ListAsync(string orgSlug, string dsSlug, string? path = null, bool recursive = false,
        EntryType? type = null, CancellationToken ct = default);

    /// <summary>The entries whose name matches <paramref name="query"/> (file system wildcards).</summary>
    Task<IEnumerable<EntryDto>> SearchAsync(string orgSlug, string dsSlug, string? query = null, string? path = null,
        bool recursive = true, EntryType? type = null, CancellationToken ct = default);

    /// <summary>The file at <paramref name="path"/> with its local path and media type.</summary>
    Task<StorageEntryDto> GetAsync(string orgSlug, string dsSlug, string? path, CancellationToken ct = default);

    /// <summary>The type of the entry at <paramref name="path"/>, null when there is none.</summary>
    Task<EntryType?> GetEntryTypeAsync(string orgSlug, string dsSlug, string path, CancellationToken ct = default);

    /// <summary>
    /// A download of <paramref name="paths"/> (all entries when null or empty): one file, or a ZIP archive
    /// written while it is sent. Anonymous bulk downloads can be disabled.
    /// </summary>
    Task<FileStreamDescriptor> DownloadAsync(string orgSlug, string dsSlug, string[]? paths,
        CancellationToken ct = default);

    /// <summary>A ZIP archive of the dataset's index (<c>.ddb</c>).</summary>
    Task<FileStreamDescriptor> GetDdbAsync(string orgSlug, string dsSlug, CancellationToken ct = default);
}
