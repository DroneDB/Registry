using Registry.Ports.DroneDB;

namespace Registry.Adapters.Testing;

/// <summary>
/// The fake's answer to <c>DDBNormalizePath</c>: a line-by-line copy of libddb's
/// <c>ddb::normalizeEntryPath</c> and <c>ReservedRules::fromJson</c> (DroneDB
/// <c>src/library/entry_path.cpp</c>), so unit tests can normalize any path without loading libddb.
/// It is a test double, not a second rule: <c>FakeDdbWrapperTests</c> runs every golden vector
/// through it, and <c>Registry.Adapters.Ddb.Test</c> runs the same vectors through native libddb.
/// </summary>
internal static class EntryPathRule
{
    private const string DatabaseFolder = ".ddb";

    public static NormalizedEntryPath Normalize(string path, IReadOnlyCollection<string>? reservedFirstSegments)
    {
        ArgumentNullException.ThrowIfNull(path);
        var reserved = ValidateReserved(reservedFirstSegments);

        // Rules 2 and 3 (nul, encoding) are answered before the C API by every adapter
        if (path.Length == 0)
            return NormalizedEntryPath.Rejection(EntryPathRejection.Empty);
        if (NormalizedEntryPath.RejectUnmarshallable(path) is { } unmarshallable)
            return unmarshallable;
        if (IsRooted(path))
            return NormalizedEntryPath.Rejection(EntryPathRejection.Absolute);

        var segments = SegmentsOf(path);
        if (segments.Contains("..", StringComparer.Ordinal))
            return NormalizedEntryPath.Rejection(EntryPathRejection.Traversal);
        if (segments.Count == 0)
            return NormalizedEntryPath.Rejection(EntryPathRejection.Empty);

        if (segments.Contains(DatabaseFolder, StringComparer.Ordinal) ||
            reserved.Contains(segments[0], StringComparer.Ordinal))
            return NormalizedEntryPath.Rejection(EntryPathRejection.Reserved);

        return NormalizedEntryPath.Accepted(string.Join('/', segments));
    }

    // ReservedRules::fromJson: each name is one whole segment, not ".."
    private static IReadOnlyCollection<string> ValidateReserved(IReadOnlyCollection<string>? names)
    {
        if (names == null)
            return [];

        foreach (var name in names)
        {
            var segments = SegmentsOf(name ?? "");
            if (name == null || segments.Count != 1 || segments[0] != name || name == ".." || name.Contains('\0'))
                throw new ArgumentException($"Invalid reserved name: \"{name}\" (expected one path segment)",
                    nameof(names));
        }

        return names;
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    // A POSIX or Windows root, a UNC or device path, or a Windows drive ("C:\x", "C:x")
    private static bool IsRooted(string path) =>
        IsSeparator(path[0]) || (path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':');

    // The segments between separators, without the empty and "." ones
    private static List<string> SegmentsOf(string path) =>
        path.Split(['/', '\\']).Where(s => s.Length > 0 && s != ".").ToList();
}
