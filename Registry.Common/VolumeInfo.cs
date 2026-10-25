#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Registry.Common;

/// <summary>
/// Size and free space of the mounted volume that backs a path.
/// <para>
/// <see cref="Path.GetPathRoot(string)"/> is not enough: it returns <c>/</c> for every path on Linux,
/// so a dataset folder on a separate mount (e.g. <c>/data/datasets</c>) would be measured on the
/// container root. <see cref="For(string)"/> picks the mount point that is the longest
/// path-segment-aware prefix of the full path instead.
/// </para>
/// </summary>
public sealed class VolumeInfo
{
    public VolumeInfo(long totalBytes, long availableFreeBytes)
    {
        TotalBytes = totalBytes;
        AvailableFreeBytes = availableFreeBytes;
    }

    public long TotalBytes { get; }

    /// <summary>Bytes available to the current user.</summary>
    public long AvailableFreeBytes { get; }

    /// <summary>
    /// The volume backing <paramref name="path"/> according to the system mount table, or
    /// <see langword="null"/> when no mount contains it or that mount is not ready.
    /// </summary>
    public static VolumeInfo? For(string path) => For(path, SystemMountRoots, MeasureSystemVolume);

    /// <summary>
    /// The volume backing <paramref name="path"/>: <paramref name="measure"/> applied to the mount point,
    /// out of <paramref name="mountRoots"/>, that is the longest prefix of the full path (see
    /// <see cref="FindMountRoot"/>). <see langword="null"/> when no mount contains the path or
    /// <paramref name="measure"/> returns <see langword="null"/>.
    /// </summary>
    /// <param name="path">Any path on the volume; need not exist. Relative paths are taken from the current directory.</param>
    /// <param name="mountRoots">Mount table (mount point paths).</param>
    /// <param name="measure">Reads size and free space of a mount point; <see langword="null"/> when not ready.</param>
    public static VolumeInfo? For(string path, Func<IEnumerable<string>> mountRoots,
        Func<string, VolumeInfo?> measure)
    {
        ArgumentNullException.ThrowIfNull(mountRoots);
        ArgumentNullException.ThrowIfNull(measure);

        var root = FindMountRoot(path, mountRoots());
        return root == null ? null : measure(root);
    }

    /// <summary>
    /// The entry of <paramref name="mountRoots"/> that contains <paramref name="path"/> (or equals it) and
    /// has the longest normalised path. Containment is path-segment-aware and follows the OS case rules
    /// (<see cref="CommonUtils.IsUnder"/>): <c>/datafoo/x</c> is not under <c>/data</c>. Symbolic links
    /// are not resolved.
    /// </summary>
    /// <returns>The matching entry as given, or <see langword="null"/>.</returns>
    public static string? FindMountRoot(string path, IEnumerable<string> mountRoots)
    {
        ArgumentNullException.ThrowIfNull(mountRoots);

        if (string.IsNullOrWhiteSpace(path))
            return null;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // A trailing separator makes "inside or equal" out of IsUnder's strict "inside":
        // /data/ is under the root /data, and / is under the root /
        if (!Path.EndsInDirectorySeparator(candidate))
            candidate += Path.DirectorySeparatorChar;

        return mountRoots
            .Where(root => CommonUtils.IsUnder(root, candidate))
            .MaxBy(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Length);
    }

    private static IEnumerable<string> SystemMountRoots() =>
        DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName);

    private static VolumeInfo? MeasureSystemVolume(string root)
    {
        var drive = new DriveInfo(root);
        return drive.IsReady ? new VolumeInfo(drive.TotalSize, drive.AvailableFreeSpace) : null;
    }
}
