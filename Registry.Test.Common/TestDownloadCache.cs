using System.Security.Cryptography;
using Registry.Common;

namespace Registry.Test.Common;

/// <summary>
/// Download cache for test fixtures (archives and single files) under the temp folder.
/// </summary>
/// <remarks>
/// <para>A cache entry is the file plus a <c>.sha256</c> sidecar with its hash. The entry is reused
/// only when both exist and the file still has that hash, so a truncated or corrupt file (for
/// example one left by a run that was killed while it downloaded) is never reused: it is deleted
/// and downloaded again.</para>
/// <para>A download goes to a unique temp file in the cache folder. When it completes, its hash is
/// committed to the sidecar and the temp file is renamed to the final name; neither rename
/// overwrites. A concurrent writer that loses the rename deletes its temp file and uses the
/// winner's file (and hash). The sidecar is committed before the archive, so a final file never
/// appears without its hash.</para>
/// </remarks>
public static class TestDownloadCache
{
    /// <summary>Suffix of the sidecar file that holds the SHA-256 of a cached file.</summary>
    public const string HashSuffix = ".sha256";

    /// <summary>Suffix of the temp files that downloads write to.</summary>
    public const string PartialSuffix = ".part";

    /// <summary>
    /// Returns the path of the cached copy of <paramref name="url"/> in <paramref name="cacheFolder"/>,
    /// downloading it over HTTP first when there is no valid cached copy.
    /// </summary>
    public static string GetOrDownload(string url, string cacheFolder) =>
        GetOrDownload(url, cacheFolder, (source, target) => HttpHelper.DownloadFileAsync(source, target));

    /// <summary>
    /// Same as <see cref="GetOrDownload(string, string)"/>, with the download step supplied by the caller.
    /// <paramref name="download"/> receives the URL and the temp file path to write.
    /// </summary>
    public static string GetOrDownload(string url, string cacheFolder, Func<string, string, Task> download)
    {
        ArgumentNullException.ThrowIfNull(download);

        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        Directory.CreateDirectory(cacheFolder);
        var finalPath = Path.Combine(cacheFolder, fileName);

        if (IsValid(finalPath))
            return finalPath;

        // Truncated or corrupt: drop it (archive first, then its hash) so the renames below can claim the names.
        if (File.Exists(finalPath))
            Evict(finalPath);

        var tempPath = Path.Combine(cacheFolder, $"{fileName}.{Guid.NewGuid():N}{PartialSuffix}");
        try
        {
            download(url, tempPath).GetAwaiter().GetResult();

            CommitHash(HashPath(finalPath), ComputeHash(tempPath));

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
                return finalPath;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Another writer won the race: use its file (the finally block drops ours).
            }
        }
        finally
        {
            Delete(tempPath);
        }

        if (!IsValid(finalPath))
            throw new InvalidDataException($"Cached file '{finalPath}' does not match its recorded hash");

        return finalPath;
    }

    /// <summary>Deletes a cached file and its hash sidecar.</summary>
    public static void Evict(string cachedPath)
    {
        Delete(cachedPath);
        Delete(HashPath(cachedPath));
    }

    /// <summary>Path of the hash sidecar of a cached file.</summary>
    public static string HashPath(string cachedPath) => cachedPath + HashSuffix;

    private static bool IsValid(string path)
    {
        var hashPath = HashPath(path);
        if (!File.Exists(path) || !File.Exists(hashPath))
            return false;

        return string.Equals(File.ReadAllText(hashPath).Trim(), ComputeHash(path), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // Writes the hash under a temp name, then renames it without overwriting: if a concurrent writer
    // committed its hash first, that one stays (it belongs to the archive that wins the next rename).
    private static void CommitHash(string hashPath, string hash)
    {
        var temp = $"{hashPath}.{Guid.NewGuid():N}{PartialSuffix}";
        try
        {
            File.WriteAllText(temp, hash);
            File.Move(temp, hashPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(hashPath))
        {
            // Another writer's hash is already in place.
        }
        finally
        {
            Delete(temp);
        }
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
