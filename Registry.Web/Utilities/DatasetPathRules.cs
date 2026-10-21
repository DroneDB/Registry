#nullable enable
using System;
using System.IO;
using System.Linq;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Import;

namespace Registry.Web.Utilities;

/// <summary>
/// The one set of dataset path rules every Registry path entry point applies: uploads and object
/// operations (<see cref="UploadPathPolicy"/>) and the archive entry normalizers (archive extract,
/// photogrammetry inputs, archive URL import).
/// </summary>
/// <remarks>
/// Interim rule (XI-09) until RQ-10's DatasetPathPolicy calls libddb's <c>DDBNormalizePath</c>.
/// The reserved-segment rule is libddb's: libddb skips every path segment named exactly ".ddb"
/// (DroneDB <c>dbops.cpp</c>, <c>containsDdbSegment</c> and the <c>DDB_FOLDER</c> filename checks),
/// so Registry must refuse such a path rather than write a file libddb never indexes.
/// </remarks>
public static class DatasetPathRules
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Whether <paramref name="path"/> (dataset relative, '/' or '\' separated) is inside a folder
    /// the Registry or libddb owns: any segment is exactly <see cref="IDDB.DatabaseFolderName"/>,
    /// or the first segment is exactly <see cref="IDDB.UploadsFolderName"/> (it only exists at the
    /// dataset root). Comparisons are ordinal, as in libddb.
    /// </summary>
    public static bool IsReserved(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var segments = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToArray();

        return segments.Any(s => string.Equals(s, IDDB.DatabaseFolderName, StringComparison.Ordinal))
               || (segments.Length > 0 &&
                   string.Equals(segments[0], IDDB.UploadsFolderName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="path"/> contains a NUL byte. A NUL
    /// truncates the string on native paths, so it could sidestep every other check. A null path
    /// passes: the caller's empty-path check reports it.
    /// </summary>
    public static void EnsureNoNulByte(string? path)
    {
        if (path is not null && path.Contains('\0'))
            throw new ArgumentException($"Unsafe path (null byte): '{path.Replace("\0", "\\0")}'.", nameof(path));
    }

    /// <summary>
    /// Normalizes an archive entry name to a safe relative path: '/' separated, every segment
    /// sanitized (<see cref="FileImportPolicy.SanitizePathSegments"/>; leading dots are kept).
    /// Returns null when the name is empty before or after sanitization (a folder entry, a name
    /// made only of invalid characters).
    /// </summary>
    /// <exception cref="ArgumentException">The name contains a NUL byte.</exception>
    /// <exception cref="InvalidOperationException">The name is rooted or has a ".." segment (zip-slip).</exception>
    public static string? NormalizeArchiveEntry(string? entryKey)
    {
        var key = (entryKey ?? string.Empty).Trim();
        EnsureNoNulByte(key);

        key = key.TrimStart(Separators);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (Path.IsPathRooted(key) || key.Split(Separators).Any(seg => seg == ".."))
            throw new InvalidOperationException($"Unsafe archive entry path (zip-slip): '{entryKey}'.");

        key = FileImportPolicy.SanitizePathSegments(key);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>
    /// The dataset path an archive entry extracts to under <paramref name="destPath"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The name contains a NUL byte.</exception>
    /// <exception cref="InvalidOperationException">The name is empty or unsafe (zip-slip), or the
    /// result is reserved (<see cref="IsReserved"/>).</exception>
    public static string JoinArchiveEntry(string? destPath, string? entryKey)
    {
        var key = NormalizeArchiveEntry(entryKey)
                  ?? throw new InvalidOperationException($"Archive entry has an empty name: '{entryKey}'.");

        var combined = CommonUtils.SafeCombine(destPath ?? string.Empty, key);

        if (IsReserved(combined))
            throw new InvalidOperationException($"Archive entry targets a reserved path: '{combined}'.");

        return combined;
    }
}
