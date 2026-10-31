using System;
using System.IO;

namespace Registry.Common;

/// <summary>
/// The two spellings of a path the Registry works with: the DroneDB form ('/' separated,
/// what libddb indexes and the API exchanges) and the local form (the OS separator, what the file
/// system takes). The one place that rewrites separators.
/// </summary>
public static class PathExtensions
{
    /// <summary>
    /// <paramref name="path"/> with every '\' replaced by '/': the separator of dataset paths and of
    /// libddb, on every OS. Nothing else changes (no normalization, see <c>DatasetPathPolicy</c>).
    /// </summary>
    public static string ToDdbPath(this string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/');
    }

    /// <summary><paramref name="path"/> with every '/' replaced by the OS separator.</summary>
    public static string ToLocalSeparators(this string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}

/// <summary>
/// How the OS compares file names: case-insensitive on Windows, ordinal elsewhere. The one
/// copy of the rule for comparing and keying local paths.
/// </summary>
public static class PathComparison
{
    /// <summary>The comparison of local paths on this OS.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The comparer of local paths on this OS (dictionary and set keys).</summary>
    public static StringComparer Comparer { get; } = StringComparer.FromComparison(Comparison);
}
