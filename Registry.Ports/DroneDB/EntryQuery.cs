#nullable enable
using System.Collections.Generic;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Which entries <see cref="IDdbIndex.Find"/> and <see cref="IDdbIndex.FindSummaries"/> return.
/// DroneDB applies every filter in the index query, so entries that do not match are never read,
/// serialized or marshalled.
/// </summary>
/// <param name="Path">Folder to list, relative to the dataset ("" or null = the dataset root).
/// A file selects itself.</param>
/// <param name="Recursive">The whole subtree of the folder, or its children only.</param>
/// <param name="NamePattern">File name pattern ('*' any characters, '?' one character, ASCII
/// letters in any case), or null for every name.</param>
/// <param name="Types">Entry types to return, or null/empty for every type.</param>
public sealed record EntryQuery(
    string? Path = null,
    bool Recursive = true,
    string? NamePattern = null,
    IReadOnlyCollection<EntryType>? Types = null)
{
    /// <summary>Every entry of the dataset.</summary>
    public static EntryQuery All { get; } = new();

    /// <summary>The whole subtree of <paramref name="path"/>.</summary>
    public static EntryQuery Under(string? path) => new(path);

    /// <summary>The entries of <paramref name="types"/> below <paramref name="path"/>.</summary>
    public static EntryQuery OfTypes(string? path, params EntryType[] types) => new(path, Types: types);
}
