using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Exceptions;

/// <summary>
/// Thrown when a file upload or import is rejected because its extension violates the
/// configured block-list or allow-list policy. The error is non-retriable.
/// </summary>
public class ExtensionBlockedException : Exception
{
    /// <summary>The file name(s) that were rejected.</summary>
    public IEnumerable<string> FileNames { get; }

    /// <summary><c>true</c> when the rejection was due to allow-list mode (not block-list).</summary>
    public bool AllowListMode { get; }

    public ExtensionBlockedException(string fileName, ImportSettings settings, bool allowListMode)
        : base(BuildSingleMessage(fileName, allowListMode))
    {
        FileNames = new[] { fileName };
        AllowListMode = allowListMode;
    }

    public ExtensionBlockedException(IEnumerable<string> fileNames, ImportSettings settings)
        : base(BuildBatchMessage(fileNames.ToArray(), settings))
    {
        var names = fileNames.ToArray();
        FileNames = names;
        AllowListMode = settings.AllowedFileExtensions.Length > 0;
    }

    /// <summary>Normalize an extension to the display string used in error messages.</summary>
    /// <remarks>Path.GetExtension returns an empty string (not null) for files without an extension.</remarks>
    private static string NormalizeExt(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? "(no extension)" : ext;
    }

    private static string BuildSingleMessage(string fileName, bool allowListMode)
    {
        var ext = NormalizeExt(fileName);
        if (allowListMode)
            return $"File '{fileName}' was rejected: extension '{ext}' is not in the allowed list. Only explicitly permitted file types are accepted on this server.";
        return $"File '{fileName}' was rejected: extension '{ext}' is on the server's block list.";
    }

    private static string BuildBatchMessage(string[] fileNames, ImportSettings settings)
    {
        var allowListMode = settings.AllowedFileExtensions.Length > 0;
        var grouped = fileNames
            .Select(fn => new { Name = fn, Ext = NormalizeExt(fn) })
            .GroupBy(x => x.Ext, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({g.Count()})")
            .OrderBy(x => x);

        if (allowListMode)
            return $"One or more files were rejected: their extensions are not in the allowed list. Files: {string.Join(", ", grouped)}. Only explicitly permitted file types are accepted on this server.";

        return $"One or more files were rejected: their extensions are on the server's block list. Files: {string.Join(", ", grouped)}.";
    }
}