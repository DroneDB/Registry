using System;
using System.IO;
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
    public static async Task<(string TempPath, long Bytes)> StreamToTempAsync(IFileSystem fs,
        string datasetFolderPath, Stream source)
    {
        // Temp lives under the dataset folder (same volume as the final file) -> a same-volume
        // File.Move at commit time is a genuine atomic rename (review round 2, finding 6).
        var tempDir = Path.Combine(datasetFolderPath, IDDB.UploadsFolderName);
        fs.FolderCreate(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".tmp");

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

}
