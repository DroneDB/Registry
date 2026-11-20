using System;
using Registry.Web.Utilities;

namespace Registry.Web.Exceptions;

/// <summary>
/// A name that already exists refused (D-040): HTTP 409 with the "already exists" message of
/// <see cref="InputRules.NameClashMessage"/> and the dataset path of the entry that holds the name, which
/// <see cref="ApiErrorResponder"/> writes as the <c>conflictPath</c> member of the ProblemDetails body.
/// </summary>
public sealed class NameClashException : ConflictException
{
    /// <summary>
    /// Creates the refusal of <paramref name="conflictPath"/>.
    /// </summary>
    /// <param name="conflictPath">Dataset-relative path of the existing entry (its own letter case).</param>
    public NameClashException(string conflictPath)
        : base(InputRules.NameClashMessage(NameOf(conflictPath)))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictPath);
        ConflictPath = conflictPath;
        // The same request clashes again until the entry is gone or the request asks to replace it
        NoRetry = true;
    }

    /// <summary>Dataset-relative path of the existing entry.</summary>
    public string ConflictPath { get; }

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
