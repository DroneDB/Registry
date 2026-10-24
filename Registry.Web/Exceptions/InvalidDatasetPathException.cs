#nullable enable
using System.Linq;
using Registry.Ports.DroneDB;

namespace Registry.Web.Exceptions;

/// <summary>
/// A path that is not a dataset path (HTTP 400, <c>noRetry</c>): the rule is libddb's
/// (<c>DDBNormalizePath</c>, applied by <see cref="Utilities.DatasetPathPolicy"/>), and
/// <see cref="Rejection"/> is the rule it breaks (<see cref="EntryPathRejection"/>).
/// </summary>
public sealed class InvalidDatasetPathException : BadRequestException
{
    public InvalidDatasetPathException(string? path, string rejection, string? subject = null)
        : base(MessageOf(path, rejection, subject), noRetry: true)
    {
        Path = path;
        Rejection = rejection;
    }

    /// <summary>The path as received.</summary>
    public string? Path { get; }

    /// <summary>The rejection code (<see cref="EntryPathRejection"/>).</summary>
    public string Rejection { get; }

    private static string MessageOf(string? path, string rejection, string? subject)
    {
        var what = subject ?? "path";
        var shown = Printable(path);
        return rejection switch
        {
            EntryPathRejection.Empty => $"The {what} is empty.",
            EntryPathRejection.Nul => $"Invalid {what} '{shown}': it contains a NUL byte.",
            EntryPathRejection.Encoding => $"Invalid {what} '{shown}': it is not valid Unicode.",
            EntryPathRejection.Absolute => $"Invalid {what} '{shown}': absolute paths are not allowed.",
            EntryPathRejection.Traversal => $"Invalid {what} '{shown}': path traversal is not allowed.",
            EntryPathRejection.Reserved => $"'{shown}' is a reserved path.",
            _ => $"Invalid {what} '{shown}' ({rejection})."
        };
    }

    // Control characters (a NUL above all) are shown escaped, never written into a message or a log
    private static string Printable(string? path) =>
        string.Concat((path ?? string.Empty).Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()));
}
