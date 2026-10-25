using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// libddb's answer to "is this a dataset path, and which one?" (<c>DDBNormalizePath</c>): the
/// normalized path ('/' separated, no empty or "." segment, no trailing separator) or the first rule
/// it breaks (<see cref="EntryPathRejection"/>). Exactly one of the two is set. The golden vectors
/// of the rule are DroneDB's <c>tests/contract/vectors/entry-paths.json</c>.
/// </summary>
public sealed record NormalizedEntryPath
{
    // Public: the generated contract (DdbJsonContext) is another class and cannot call a private constructor
    [JsonConstructor]
    public NormalizedEntryPath(string? path, string? rejected)
    {
        if ((path == null) == (rejected == null))
            throw new DdbException("DDBNormalizePath returned neither or both of 'path' and 'rejected'");

        Path = path;
        Rejected = rejected;
    }

    /// <summary>The normalized path; null when the path is rejected.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; }

    /// <summary>The rejection code (<see cref="EntryPathRejection"/>); null when the path is accepted.</summary>
    [JsonPropertyName("rejected")]
    public string? Rejected { get; }

    public static NormalizedEntryPath Accepted(string path) =>
        new(path ?? throw new System.ArgumentNullException(nameof(path)), null);

    public static NormalizedEntryPath Rejection(string code) =>
        new(null, code ?? throw new System.ArgumentNullException(nameof(code)));

    /// <summary>
    /// libddb's rules that a .NET caller answers itself because the string cannot cross the C API
    /// unchanged: a NUL byte ("nul", the marshaller would pass a shorter path) and a lone UTF-16
    /// surrogate ("encoding", the marshaller would pass U+FFFD instead). Null when the path can be
    /// passed to <c>DDBNormalizePath</c> (an empty path too: that rejection is libddb's).
    /// </summary>
    public static NormalizedEntryPath? RejectUnmarshallable(string path)
    {
        System.ArgumentNullException.ThrowIfNull(path);

        if (path.Contains('\0'))
            return Rejection(EntryPathRejection.Nul);

        for (var i = 0; i < path.Length; i++)
        {
            if (!char.IsSurrogate(path[i]))
                continue;
            if (!char.IsHighSurrogate(path[i]) || i + 1 >= path.Length || !char.IsLowSurrogate(path[i + 1]))
                return Rejection(EntryPathRejection.Encoding);
            i++;
        }

        return null;
    }
}

/// <summary>The rejection codes of <c>DDBNormalizePath</c>, in the order libddb checks them.</summary>
public static class EntryPathRejection
{
    /// <summary>The path is empty, or nothing is left once empty and "." segments are dropped.</summary>
    public const string Empty = "empty";

    /// <summary>The path contains a NUL byte (the C API cannot receive one: callers check it first).</summary>
    public const string Nul = "nul";

    /// <summary>The path is not valid UTF-8.</summary>
    public const string Encoding = "encoding";

    /// <summary>A leading '/' or '\' (POSIX root, UNC, device path) or a drive ("C:").</summary>
    public const string Absolute = "absolute";

    /// <summary>A ".." segment.</summary>
    public const string Traversal = "traversal";

    /// <summary>A ".ddb" segment at any depth, or a name the caller reserves as the first segment.</summary>
    public const string Reserved = "reserved";
}
