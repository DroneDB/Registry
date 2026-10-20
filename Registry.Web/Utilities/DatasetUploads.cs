using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Utilities;

public static class DatasetUploads
{
    /// <summary>Temporary upload files and push folders untouched for this long are abandoned.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

    public const string TempFileExtension = ".tmp";

    /// <summary>Folder of the resumable (chunked) uploads, under the uploads folder.</summary>
    public const string ChunksFolderName = "chunks";

    /// <summary>
    /// Streams a forward-only upload body to a temporary file in the dataset's
    /// <see cref="IDDB.UploadsFolderName"/> folder: same volume as the dataset, so it can later be
    /// moved (not copied) into its final place. The body is hashed while it is written. A failed
    /// stream leaves no file behind.
    /// </summary>
    /// <returns>The temp file, its size and its SHA-256.</returns>
    public static async Task<StreamedFile> StreamToTempAsync(IFileSystem fs, string datasetFolderPath, Stream source,
        CancellationToken ct = default)
    {
        // Temp lives under the dataset folder (same volume as the final file) -> a same-volume
        // File.Move at commit time is a genuine atomic rename (review round 2, finding 6).
        var tempDir = Path.Combine(datasetFolderPath, IDDB.UploadsFolderName);
        fs.FolderCreate(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + TempFileExtension);

        string sha256;
        long hashed;
        try
        {
            // CreateNew, not Create: the path is new, and on ext4 truncating a file (what Create
            // does) makes its close write back every dirty page first (auto_da_alloc), which
            // measured 0.2 s per GiB after the last byte of an upload
            await using var ts = new HashingWriteStream(fs.OpenWrite(tempPath, FileMode.CreateNew));
            await source.CopyToAsync(ts, 1024 * 1024, ct);
            hashed = ts.BytesWritten;
            sha256 = ts.GetHexDigest();
        }
        catch
        {
            // Aborted/oversized stream: drop the partial temp file
            try { File.Delete(tempPath); }
            catch { /* ignore */ }
            throw;
        }

        // The write time after the file is closed: DroneDB trusts the hash only while it is unchanged
        var hint = new FileHashHint(sha256, hashed, fs.GetLastWriteTime(tempPath).ToUniversalTime());
        return new StreamedFile(tempPath, fs.GetFileSize(tempPath), hint);
    }

    /// <summary>
    /// Deletes what interrupted uploads leave in a dataset: push folders
    /// (<c>.ddb/tmp/push-*</c>, a push that was never committed) and streamed temp files
    /// (<c>.uploads/*.tmp</c>) and resumable uploads (<c>.uploads/chunks/*</c>) with no write for
    /// <paramref name="maxAge"/>. The quarantine
    /// folder is left to the reconciliation sweep.
    /// </summary>
    /// <returns>How many folders and files were deleted.</returns>
    public static int RemoveAbandoned(IFileSystem fs, string datasetFolderPath, DateTime utcNow, TimeSpan maxAge)
    {
        var removed = 0;
        bool Abandoned(string path) => utcNow - fs.GetLastWriteTime(path).ToUniversalTime() > maxAge;

        var tmpFolder = Path.Combine(datasetFolderPath, IDDB.DatabaseFolderName, IDDB.TmpFolderName);
        if (fs.FolderExists(tmpFolder))
        {
            foreach (var pushFolder in fs.GetDirectories(tmpFolder, IDDB.PushFolderPrefix + "*"))
            {
                // Last activity: the folder or the newest file in it (an upload in progress)
                if (!Abandoned(pushFolder) ||
                    fs.EnumerateFiles(pushFolder, "*", SearchOption.AllDirectories).Any(f => !Abandoned(f)))
                    continue;

                fs.FolderDelete(pushFolder);
                removed++;
            }
        }

        var uploadsFolder = Path.Combine(datasetFolderPath, IDDB.UploadsFolderName);
        if (fs.FolderExists(uploadsFolder))
        {
            foreach (var tempFile in fs.EnumerateFiles(uploadsFolder, "*" + TempFileExtension).Where(Abandoned).ToArray())
            {
                fs.Delete(tempFile);
                removed++;
            }

            // Resumable uploads nobody came back to (last chunk written that long ago)
            var chunksFolder = Path.Combine(uploadsFolder, ChunksFolderName);
            if (fs.FolderExists(chunksFolder))
            {
                foreach (var upload in fs.GetDirectories(chunksFolder, "*"))
                {
                    if (!Abandoned(upload) ||
                        fs.EnumerateFiles(upload, "*", SearchOption.AllDirectories).Any(f => !Abandoned(f)))
                        continue;

                    fs.FolderDelete(upload);
                    removed++;
                }
            }
        }

        return removed;
    }
}

/// <summary>An upload body written to a temporary file in the dataset's uploads folder.</summary>
/// <param name="TempPath">The temporary file.</param>
/// <param name="Bytes">Its size on disk.</param>
/// <param name="Hash">The SHA-256 computed while it was written.</param>
public sealed record StreamedFile(string TempPath, long Bytes, FileHashHint Hash);
