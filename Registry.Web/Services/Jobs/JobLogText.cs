using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Registry.Common;
using Registry.Web.Exceptions;

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
        var message = RawCause(ex);
        return message == null ? "Unknown error" : Truncate(message, 500);
    }

    // The innermost non-blank message of ex as one line, not truncated; null when every message is blank
    private static string? RawCause(Exception? ex)
    {
        for (var current = ex?.GetBaseException(); current != null; current = Outer(ex!, current))
        {
            var message = SanitizeSingleLine(current.Message);
            if (message.Length > 0) return message;
        }

        return null;
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

    /// <summary>The longest <see cref="UserMessage(Exception?, string?)"/> (the <c>JobIndex.ErrorMessage</c> column).</summary>
    public const int UserMessageMaxLength = 2048;

    /// <summary>What replaces a path in the internal build folder of a dataset (<c>.ddb/build/&lt;hash&gt;/…</c>).</summary>
    public const string BuildFolderPlaceholder = "[build folder]";

    /// <summary>What replaces any other absolute path of the server.</summary>
    public const string ServerPathPlaceholder = "[server path]";

    /// <summary>The text of the known messages that say a raster cannot be read.</summary>
    public const string DamagedRasterMessage = "The file is damaged or not a valid GeoTIFF";

    /// <summary>
    /// Why a task failed, for any reader of the task (the task <c>error</c>, the build-failure e-mail): the cause of
    /// <paramref name="ex"/> (<see cref="Cause"/>), mapped to plain text when it is a known message, without server
    /// paths (see <see cref="UserMessage(string?, string?)"/>), at most <see cref="UserMessageMaxLength"/> characters.
    /// An unsafe archive entry says which entry it is and that nothing was extracted.
    /// </summary>
    public static string UserMessage(Exception? ex, string? datasetFolderPath)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            // The entry is the archive's, not a server path: it is shown as it is (control characters escaped)
            if (current is InvalidDatasetPathException unsafePath &&
                unsafePath.Message.Contains(ArchiveEntrySubject, StringComparison.Ordinal))
                return Truncate(
                    $"The archive contains an unsafe path: {Printable(unsafePath.Path)}. Nothing was extracted.",
                    UserMessageMaxLength - Ellipsis.Length);
        }

        return UserMessage(RawCause(ex), datasetFolderPath);
    }

    /// <summary>
    /// <paramref name="message"/> (an exception message, or a line of a job log) as a reader who is not an administrator
    /// may see it: one line, without a libddb <c>(internal)</c> prefix, a known GDAL/libddb message replaced by plain
    /// text, and no server path. The dataset folder (as given, as <see cref="Path.GetFullPath(string)"/> makes it, and
    /// with its links resolved) becomes <see cref="DatasetPlaceholder"/>; a path in the internal build folder of a
    /// dataset becomes <see cref="BuildFolderPlaceholder"/>; any other absolute path (Unix, Windows drive or UNC) of at
    /// least two segments becomes <see cref="ServerPathPlaceholder"/>. At most <see cref="UserMessageMaxLength"/>
    /// characters; "Unknown error" for a blank text.
    /// </summary>
    public static string UserMessage(string? message, string? datasetFolderPath)
    {
        var text = SanitizeSingleLine(message ?? string.Empty);
        text = InternalPrefix.Replace(text, string.Empty).Trim();
        if (text.Length == 0) return "Unknown error";

        foreach (var known in DamagedRasterMessages)
            if (text.Contains(known, StringComparison.OrdinalIgnoreCase))
                return DamagedRasterMessage;

        foreach (var folder in FolderForms(datasetFolderPath))
            text = ScrubDatasetPath(text, folder);

        text = DatasetBuildPath.Replace(text, BuildFolderPlaceholder);
        text = AbsolutePath.Replace(text, ScrubAbsolutePath);
        return Truncate(text, UserMessageMaxLength - Ellipsis.Length);
    }

    // The subject of InvalidDatasetPathException that DatasetArchiveExtractor throws for an archive entry
    private const string ArchiveEntrySubject = "archive entry";

    // GDAL/libddb messages that mean the raster cannot be read: cog.cpp "<utility> failed to create output COG: <path>",
    // GDAL "'<path>' not recognized as a supported file format"
    private static readonly string[] DamagedRasterMessages =
        ["failed to create output COG", "not recognized as a supported file format"];

    private static readonly Regex InternalPrefix =
        new(@"^\(internal\)\s*:?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The characters of a path in a message: a space, a quote, a colon after the drive, a list or group separator ends it
    private const string PathChars = @"[^\s'""`<>|:;,()\[\]{}*?]";

    // A path below the placeholder of the dataset folder, in its internal build folder
    private static readonly Regex DatasetBuildPath = new(
        Regex.Escape(DatasetPlaceholder) + @"[\\/]\.ddb[\\/]build(?:[\\/]" + PathChars + "*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // An absolute path: Unix, Windows drive, UNC or a file:// URI. In quotes it runs to the closing quote (a folder
    // name may have spaces: 'C:\Users\Jane Doe\x.tif'); without quotes to the first space. Not one that continues a
    // word, a URL ("scheme://host/…"), a number ("1/2") or a placeholder ("[dataset]/a.tif"); a drive path may follow
    // a colon ("error:C:\srv\x.tif").
    private static readonly Regex AbsolutePath = new(
        @"(?<q>['""`])(?<quoted>(?:file:///?(?:[A-Za-z]:[\\/])?|[A-Za-z]:[\\/]|\\\\|/)[^'""`\r\n]*?)\k<q>" +
        @"|(?<![\w.\\/\]-])(?:file:///?(?:[A-Za-z]:[\\/])?|[A-Za-z]:[\\/]|(?<!:)\\\\|(?<!:)/)" + PathChars + "+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string ScrubAbsolutePath(Match match)
    {
        if (match.Groups["quoted"].Success)
        {
            var quote = match.Groups["q"].Value;
            return quote + PathPlaceholder(match.Groups["quoted"].Value) + quote;
        }

        var value = match.Value;
        // The full stop of a sentence is not part of the path
        var trailing = value.Length - value.TrimEnd('.').Length;
        var path = value[..^trailing];
        return PathPlaceholder(path) + value[path.Length..];
    }

    // What replaces path: the placeholder of the build folder or of a server path; "/tmp" or "/sys" (one segment of a
    // Unix path says nothing of the server) stays
    private static string PathPlaceholder(string path)
    {
        var isUnix = path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal);
        if (isUnix && path.IndexOf('/', 1) < 0)
            return path;

        return path.ToDdbPath().Contains("/.ddb/build", StringComparison.OrdinalIgnoreCase)
            ? BuildFolderPlaceholder
            : ServerPathPlaceholder;
    }

    // The folder as given, as GetFullPath makes it, and with its links resolved: libddb may print any of them
    private static IEnumerable<string> FolderForms(string? datasetFolderPath)
    {
        if (string.IsNullOrWhiteSpace(datasetFolderPath)) yield break;

        var forms = new List<string> { datasetFolderPath };
        try
        {
            var full = Path.GetFullPath(datasetFolderPath);
            forms.Add(full);
            if (RealPath(full) is { } real) forms.Add(real);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not a path of this machine: the form as given is all there is
        }

        // The longest first: a form that is a prefix of another must not cut it
        foreach (var form in forms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
            yield return form;
    }

    // fullPath with every link of its folders resolved (realpath); null when it cannot be read
    private static string? RealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(fullPath)) return null;

        var current = root;
        foreach (var part in fullPath[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = target.FullName;
        }

        return current;
    }

    // Control characters (a NUL above all) escaped, as InvalidDatasetPathException shows them
    private static string Printable(string? value) =>
        string.Concat((value ?? string.Empty).Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()));

    private const string Ellipsis = "...";

    private static string Truncate(string value, int max) =>
        value.Length > max ? string.Concat(value.AsSpan(0, max), Ellipsis) : value;

    // Collapses newlines/CR so an exception message stays a single ring-buffer display line.
    private static string SanitizeSingleLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
