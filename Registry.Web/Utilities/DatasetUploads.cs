using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

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
    public static async Task<StreamedFile> StreamToTempAsync(ScratchFileProvider scratch, IFileSystem fs,
        string datasetFolderPath, Stream source, CancellationToken ct = default)
    {
        // Temp lives under the dataset folder (same volume as the final file) -> a same-volume
        // File.Move at commit time is a genuine atomic rename.
        // An aborted or oversized stream drops the partial file (the scratch file's Dispose).
        using var temp = scratch.CreateForDataset(datasetFolderPath, TempFileExtension);

        string sha256;
        long hashed;
        // CreateNew, not Create: the path is new, and on ext4 truncating a file (what Create
        // does) makes its close write back every dirty page first (auto_da_alloc), which
        // measured 0.2 s per GiB after the last byte of an upload
        await using (var ts = new HashingWriteStream(fs.OpenWrite(temp.Path, FileMode.CreateNew)))
        {
            await source.CopyToAsync(ts, 1024 * 1024, ct);
            hashed = ts.BytesWritten;
            sha256 = ts.GetHexDigest();
        }

        // The write time after the file is closed: DroneDB trusts the hash only while it is unchanged
        var hint = new FileHashHint(sha256, hashed, fs.GetLastWriteTime(temp.Path).ToUniversalTime());
        var file = new StreamedFile(temp.Path, fs.GetFileSize(temp.Path), hint);
        temp.Release(); // the commit moves it into place or deletes it
        return file;
    }

    /// <summary>
    /// Deletes what interrupted uploads leave in a dataset: push folders
    /// (<c>.ddb/tmp/push-*</c>, a push that was never committed) and streamed temp files
    /// (<c>.uploads/*.tmp</c>) with no write for <paramref name="maxAge"/>, and resumable uploads
    /// (<c>.uploads/chunks/*</c>) with no chunk written: a session that the client can resume (its manifest keeps the
    /// file id) after <paramref name="chunksMaxAge"/> (7 days, <see cref="ChunkStore.AbandonedAfter"/>, §8.3: a user
    /// can come back to a paused upload within the week), any other one after <paramref name="maxAge"/>: a session
    /// from before the fix (no file id in its manifest, no client can reach it), the chunks of a share batch (its
    /// rollback goes after 24 hours too) and a folder without a readable manifest. The quarantine
    /// folder is left to the reconciliation sweep. A resumable upload is removed through <paramref name="chunks"/>
    /// (the store of the uploads of this process; none: a store of its own), under the upload's lock and with its state
    /// in memory.
    /// </summary>
    /// <returns>How many folders and files were deleted.</returns>
    public static int RemoveAbandoned(IFileSystem fs, string datasetFolderPath, DateTime utcNow, TimeSpan maxAge,
        TimeSpan? chunksMaxAge = null, ChunkStore? chunks = null)
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
            var resumableAge = chunksMaxAge ?? ChunkStore.AbandonedAfter;
            var chunksFolder = Path.Combine(uploadsFolder, ChunksFolderName);
            if (fs.FolderExists(chunksFolder))
            {
                var store = chunks ?? new ChunkStore();
                foreach (var upload in fs.GetDirectories(chunksFolder, "*"))
                {
                    var age = Resumable(fs, upload) ? resumableAge : maxAge;
                    bool ChunkAbandoned(string path) => utcNow - fs.GetLastWriteTime(path).ToUniversalTime() > age;
                    bool UploadAbandoned(string folder) => ChunkAbandoned(folder) &&
                        !fs.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => !ChunkAbandoned(f));

                    // Checked again under the upload's lock: a chunk may arrive meanwhile (the first check does not wait
                    // for a chunk being stored)
                    if (UploadAbandoned(upload) && store.RemoveAbandoned(upload, UploadAbandoned))
                        removed++;
                }
            }
        }

        return removed;
    }

    // Whether a chunk session is one a client can resume: its manifest keeps the file id the client gave (§7.1)
    private static bool Resumable(IFileSystem fs, string upload)
    {
        var manifest = Path.Combine(upload, ChunkStore.ManifestFileName);
        try
        {
            return fs.Exists(manifest) && ApiJson.Deserialize<ChunkManifest>(fs.ReadAllText(manifest))?.FileId != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false; // removed meanwhile or unreadable: not a session a client can resume
        }
    }
}

/// <summary>An upload body written to a temporary file in the dataset's uploads folder.</summary>
/// <param name="TempPath">The temporary file.</param>
/// <param name="Bytes">Its size on disk.</param>
/// <param name="Hash">The SHA-256 computed while it was written.</param>
public sealed record StreamedFile(string TempPath, long Bytes, FileHashHint Hash);
