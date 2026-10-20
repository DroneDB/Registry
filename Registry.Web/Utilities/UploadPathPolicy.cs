#nullable enable
using System;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Utilities;

/// <summary>
/// Where a file can be written in a dataset. One rule set for the checks that run before an
/// upload's first byte (UPL-01 E.2) and the ones that run when it is committed.
/// </summary>
public static class UploadPathPolicy
{
    /// <summary>Whether <paramref name="path"/> is inside a folder the Registry owns (index, uploads).</summary>
    public static bool IsReserved(string path) =>
        path.StartsWith(IDDB.DatabaseFolderName) || path.StartsWith(IDDB.UploadsFolderName);

    /// <summary>
    /// Throws when a file cannot be uploaded to <paramref name="path"/>: path traversal, a reserved
    /// folder, or an extension the import policy blocks.
    /// </summary>
    public static void Validate(string path, string datasetFolderPath, ImportSettings import)
    {
        // Validate path to prevent path traversal attacks
        CommonUtils.ValidateRelativePath(path, datasetFolderPath);

        if (IsReserved(path))
            throw new InvalidOperationException($"'{path}' is a reserved path");

        // Extension policy gate - reject before anything is written into the dataset
        if (!import.IsExtensionAllowed(path))
            throw new ExtensionBlockedException(path, import, import.AllowedFileExtensions.Length > 0);
    }
}
