using System;
using System.IO;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// A file named like a raster that is none: a call on it fails and libddb logs the failure, which is
/// what the log tests need from the library. libddb reads the files of users only in its gdal-util
/// helper program, so GDAL's own error about the file stays in the helper; on the calling thread
/// libddb logs, at the debug level and with the operation of the call, that the helper could not read
/// the file. At that level the library logs more, from other threads too: the tests take the records
/// that name the file.
/// </summary>
internal static class UnreadableRaster
{
    /// <summary>The level libddb logs the failure at.</summary>
    public const DdbLogLevel LogLevel = DdbLogLevel.Debug;

    /// <summary>Writes the file in <paramref name="folder"/> and returns its path.</summary>
    public static string Create(string folder)
    {
        var path = Path.Combine(folder, "not-a-raster.tif");
        File.WriteAllText(path, "not a raster");
        return path;
    }

    /// <summary>Whether a native record is about the file at <paramref name="path"/>.</summary>
    public static bool IsAbout(string message, string path) => message.Contains(path, StringComparison.Ordinal);
}
