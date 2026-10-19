using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Utilities;

public static class DatasetUploads
{
    /// <summary>
    /// Streams a forward-only upload body to a temporary file in the dataset's
    /// <see cref="IDDB.UploadsFolderName"/> folder: same volume as the dataset, so it can later be
    /// moved (not copied) into its final place. A failed stream leaves no file behind.
    /// </summary>
    /// <returns>The temp file path and the number of bytes written.</returns>
    /// <summary>Temporary upload files and push folders untouched for this long are abandoned.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

    public const string TempFileExtension = ".tmp";

    public static async Task<(string TempPath, long Bytes)> StreamToTempAsync(IFileSystem fs,
        string datasetFolderPath, Stream source)
    {
        // Temp lives under the dataset folder (same volume as the final file) -> a same-volume
        // File.Move at commit time is a genuine atomic rename (review round 2, finding 6).
        var tempDir = Path.Combine(datasetFolderPath, IDDB.UploadsFolderName);
        fs.FolderCreate(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + TempFileExtension);

        try
        {
            await using var ts = fs.OpenWrite(tempPath);
            await source.CopyToAsync(ts, 1024 * 1024);
        }
        catch
        {
            // Aborted/oversized stream: drop the partial temp file
            try { File.Delete(tempPath); }
            catch { /* ignore */ }
            throw;
        }

        return (tempPath, fs.GetFileSize(tempPath));
    }

    /// <summary>
    /// Deletes what interrupted uploads leave in a dataset: push folders
    /// (<c>.ddb/tmp/push-*</c>, a push that was never committed) and streamed temp files
    /// (<c>.uploads/*.tmp</c>) with no write for <paramref name="maxAge"/>. The quarantine
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
        }

        return removed;
    }
}
