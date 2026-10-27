#nullable enable
using System;
using Registry.Common;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Text rules of the job logs: an exception as one display line, and the dataset folder removed
/// from a line (the log tail of a build is readable by anonymous users on public datasets).
/// </summary>
public static class JobLogText
{
    /// <summary>What replaces the dataset folder in a scrubbed line.</summary>
    public const string DatasetPlaceholder = "[dataset]";

    /// <summary>
    /// Formats an exception as a single display line for the job log tail:
    /// <c>TypeName: message [InnerTypeName: inner message]</c>, message truncated to
    /// 500 chars and inner message to 300. Ring-buffer lines are display lines, so
    /// newlines are collapsed.
    /// </summary>
    public static string Describe(Exception? ex)
    {
        if (ex == null) return "Unknown error";

        var message = Truncate(SanitizeSingleLine(ex.Message), 500);

        // The inner exception carries the cause: adapters wrap the native error, e.g.
        // "InvalidOperationException: Cannot build 'a.dwg' ... [DdbException: DWG support
        // needs dwg2dxf ...]". Without its message the log only says that a build failed.
        var inner = string.Empty;
        if (ex.InnerException != null)
        {
            var innerMessage = Truncate(SanitizeSingleLine(ex.InnerException.Message), 300);
            inner = innerMessage.Length > 0
                ? $" [{ex.InnerException.GetType().Name}: {innerMessage}]"
                : $" [{ex.InnerException.GetType().Name}]";
        }

        return $"{ex.GetType().Name}: {message}{inner}";
    }

    /// <summary>
    /// The cause of <paramref name="ex"/> for a reader who is not an administrator: the message of the innermost
    /// exception (the native error, which says what is wrong with the file), as one line of at most 500 chars, without
    /// type names. Falls back to the outer messages when the inner ones are blank.
    /// </summary>
    public static string Cause(Exception? ex)
    {
        for (var current = ex?.GetBaseException(); current != null; current = Outer(ex!, current))
        {
            var message = SanitizeSingleLine(current.Message);
            if (message.Length > 0) return Truncate(message, 500);
        }

        return "Unknown error";
    }

    // The exception that wraps inner in the chain that starts at ex; null when inner is ex.
    private static Exception? Outer(Exception ex, Exception inner)
    {
        for (var current = ex; current.InnerException != null; current = current.InnerException)
            if (ReferenceEquals(current.InnerException, inner)) return current;
        return null;
    }

    /// <summary>
    /// <paramref name="value"/> with <paramref name="datasetFolderPath"/> replaced by
    /// <see cref="DatasetPlaceholder"/>. Native messages may use either separator style, so both
    /// variants are replaced.
    /// </summary>
    public static string ScrubDatasetPath(string value, string? datasetFolderPath)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(datasetFolderPath)) return value;

        var trimmed = datasetFolderPath.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return value;

        var ddbPath = trimmed.ToDdbPath();
        return value
            .Replace(ddbPath, DatasetPlaceholder, StringComparison.OrdinalIgnoreCase)
            .Replace(ddbPath.Replace('/', '\\'), DatasetPlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int max) =>
        value.Length > max ? string.Concat(value.AsSpan(0, max), "...") : value;

    // Collapses newlines/CR so an exception message stays a single ring-buffer display line.
    private static string SanitizeSingleLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
