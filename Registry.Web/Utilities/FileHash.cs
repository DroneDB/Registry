#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Utilities;

/// <summary>
/// The SHA-256 of a file already on disk, read back (lowercase hex). Only for files written by
/// someone else (libddb's GeoTIFF export writes blocks at random offsets): what Registry writes
/// itself is hashed while it is written (<see cref="HashingWriteStream"/>), so it is not read again.
/// </summary>
public static class FileHash
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
