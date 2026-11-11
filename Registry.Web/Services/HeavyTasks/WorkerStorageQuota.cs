using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// What the storage quota of the owner of the target organization asks of a task while it writes (FR-XC-26, D-039): the
/// cap of an extraction (the archive extract tool and the photogrammetry results hand it to
/// <see cref="ArchiveExtractionRequest.MaxBytes"/>), and the check of the one file a tool wrote. They read the remaining
/// bytes when the task writes, never a value kept from the submit, and need no current user.
/// </summary>
public static class WorkerStorageQuota
{
    /// <summary>
    /// The cap on the bytes an extraction into <paramref name="orgSlug"/> writes: the smaller of
    /// <paramref name="archiveCapBytes"/> (the archive size limit; 0 or less: none) and the bytes the owner may still
    /// store, read now, when the extraction starts to write, never a value kept from the submit, plus
    /// <paramref name="replacedBytes"/>: the size the owner's use counts now for the files that the extraction replaces
    /// (overwrite), so that only the growth counts, as for a copy. <paramref name="replacedBytes"/> is read only when a
    /// limit applies. The message is the 507 text when the quota is the smaller one, null otherwise (the text of the
    /// archive size limit).
    /// </summary>
    /// <exception cref="QuotaExceededException">The owner has nothing left (and the extraction replaces nothing).</exception>
    public static async Task<(long MaxBytes, string? Message)> CapAsync(IStorageQuota storage, string orgSlug,
        long archiveCapBytes, Func<long>? replacedBytes, CancellationToken ct)
    {
        var remaining = await storage.RemainingBytesOfOrganizationAsync(orgSlug, ct);
        if (remaining is not { } left)
            return (archiveCapBytes, null);

        var room = left + Math.Max(0, replacedBytes?.Invoke() ?? 0);

        // A cap of 0 would mean "no cap" to the extraction
        if (room <= 0)
            throw QuotaExceededException.OwnerOverQuota(left);

        return archiveCapBytes > 0 && archiveCapBytes <= room
            ? (archiveCapBytes, null)
            : (room, QuotaExceededException.OwnerOverQuotaMessage(left));
    }

    /// <summary>
    /// Throws when the file <paramref name="outputPath"/> that a tool just wrote does not fit in what the owner of
    /// <paramref name="orgSlug"/> may still store, read now: a tool whose native step writes one file of a size that no
    /// guard bounds while it is written (Merge, Align) fails the task. Only the growth counts: a file that the output
    /// replaced (overwrite) is still counted in the owner's use by its size <paramref name="replacedBytes"/> (its index
    /// entry), so the check is <c>max(0, new size - replacedBytes)</c> against what is left, as for a copy.
    /// </summary>
    /// <exception cref="QuotaExceededException">The growth does not fit.</exception>
    public static Task EnsureOutputFitsAsync(IStorageQuota storage, string orgSlug, string outputPath,
        long replacedBytes, CancellationToken ct) =>
        EnsureOutputFitsAsync(storage, orgSlug, [outputPath], replacedBytes, ct);

    /// <summary>
    /// <see cref="EnsureOutputFitsAsync(IStorageQuota, string, string, long, CancellationToken)"/> for an output and the
    /// files written with it (the <c>.msk</c> sidecar of a masked raster): their sizes, of those that exist, count together.
    /// </summary>
    /// <exception cref="QuotaExceededException">The growth does not fit.</exception>
    public static async Task EnsureOutputFitsAsync(IStorageQuota storage, string orgSlug,
        IReadOnlyList<string> outputPaths, long replacedBytes, CancellationToken ct)
    {
        var remaining = await storage.RemainingBytesOfOrganizationAsync(orgSlug, ct);
        if (remaining is not { } left)
            return;

        long written = 0;
        var any = false;
        foreach (var path in outputPaths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                continue;
            any = true;
            written += info.Length;
        }

        if (any && GrowthBytes(written, replacedBytes) > left)
            throw QuotaExceededException.OwnerOverQuota(left);
    }

    /// <summary>What writing <paramref name="newBytes"/> over a file the owner's use counts as <paramref name="replacedBytes"/> adds to it: never below 0.</summary>
    public static long GrowthBytes(long newBytes, long replacedBytes) => Math.Max(0, newBytes - Math.Max(0, replacedBytes));

    /// <summary>The size the owner's use counts for the file at <paramref name="path"/> (its index entry), 0 when it is not indexed.</summary>
    public static long IndexedBytes(Registry.Ports.DroneDB.IDDB ddb, string path) =>
        ddb.GetEntry(path) is { Type: not Registry.Ports.DroneDB.EntryType.Directory, Size: > 0 } entry ? entry.Size : 0;
}
