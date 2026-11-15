using System;
using System.IO;
using Registry.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// The version of a file a renderer reads: its last write time in UTC ticks. A build rewrites its artifact under the
/// hash of the entry it came from, so the hash alone cannot tell a rebuilt file from the one a cache or a client
/// still holds; this value can. It is the one definition: the validators of the responses, the keys of the server
/// render caches and the OGC raster sources all use it, so the one file has one version everywhere.
/// </summary>
public static class FileVersion
{
    /// <summary>The version of <paramref name="localPath"/>; 0 when it cannot be read (<paramref name="onError"/> is told why).</summary>
    public static long Of(IFileSystem fs, string localPath, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(fs);

        try
        {
            return fs.GetLastWriteTime(localPath).ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            onError?.Invoke(ex);
            return 0;
        }
    }
}
