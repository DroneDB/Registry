#nullable enable
using System;
using System.IO;
using System.Linq;

namespace Registry.Web.Utilities;

public static class DatasetThumbnails
{
    /// <summary>
    /// Whether <paramref name="path"/> is a file that can be a dataset's thumbnail: one of the
    /// configured candidate names, in the dataset root.
    /// </summary>
    public static bool IsCandidateFile(string? path, string[] candidates)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // Only treat files in the dataset root (no directory component) as thumbnail candidates
        if (path.Contains('/') || path.Contains('\\'))
            return false;

        return candidates.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }
}
