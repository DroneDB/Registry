#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models;

/// <summary>
/// Selects the index entries a rescan re-reads: an entry must match one of
/// <see cref="Types"/> (when any) and one of <see cref="Extensions"/> (when any).
/// Mirrors the DroneDB native filter (<c>DDBRescanEx</c>), which applies it again
/// inside the rescan, so <see cref="Matches"/> only decides what to submit.
/// </summary>
public sealed class RescanFilter
{
    private static readonly SearchValues<char> InvalidExtensionChars = SearchValues.Create("/\\*? \t");

    private RescanFilter(EntryType[] types, string[] extensions)
    {
        Types = types;
        Extensions = extensions;
    }

    /// <summary>Entry types to rescan, or empty for all.</summary>
    public IReadOnlyList<EntryType> Types { get; }

    /// <summary>Lowercase extensions without the leading dot (e.g. "dwg", "copc.laz"), or empty for all.</summary>
    public IReadOnlyList<string> Extensions { get; }

    public bool IsEmpty => Types.Count == 0 && Extensions.Count == 0;

    /// <summary>The native "types" argument (comma-separated, lowercase), or null for all.</summary>
    public string? TypesArgument => Types.Count == 0
        ? null
        : string.Join(",", Types.Select(t => t.ToString().ToLowerInvariant()));

    /// <summary>The native "extensions" argument (comma-separated), or null for all.</summary>
    public string? ExtensionsArgument => Extensions.Count == 0 ? null : string.Join(",", Extensions);

    /// <summary>The native rescan options for this filter.</summary>
    public RescanOptions ToOptions(bool stopOnError) => new(TypesArgument, ExtensionsArgument, stopOnError);

    /// <summary>
    /// Returns this filter, or throws when it selects every entry: a rescan that spans
    /// datasets must not re-hash all stored data.
    /// </summary>
    /// <exception cref="ArgumentException">The filter is empty.</exception>
    public RescanFilter RequireNotEmpty() => IsEmpty
        ? throw new ArgumentException("Specify at least one entry type or file extension to rescan")
        : this;

    /// <summary>Parses comma-separated lists, as the rescan endpoints and the rescan-index tool take them.</summary>
    /// <exception cref="ArgumentException">An unknown type, a directory type or an invalid extension.</exception>
    public static RescanFilter Parse(string? types, string? extensions) =>
        Parse(types == null ? null : [types], extensions == null ? null : [extensions]);

    /// <summary>Parses lists whose items may themselves be comma-separated.</summary>
    /// <exception cref="ArgumentException">An unknown type, a directory type or an invalid extension.</exception>
    public static RescanFilter Parse(IEnumerable<string>? types, IEnumerable<string>? extensions)
    {
        var parsedTypes = new List<EntryType>();
        foreach (var raw in SplitItems(types))
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;

            // Enum names match the native type names ignoring case; numbers are not type names.
            if (!Enum.TryParse<EntryType>(item, true, out var type) || !Enum.IsDefined(type) ||
                item.All(char.IsDigit) || type == EntryType.Undefined)
                throw new ArgumentException($"Unknown entry type: {item}");
            if (type == EntryType.Directory)
                throw new ArgumentException("Cannot rescan directories");

            if (!parsedTypes.Contains(type)) parsedTypes.Add(type);
        }

        var parsedExtensions = new List<string>();
        foreach (var raw in SplitItems(extensions))
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;

            var ext = item.StartsWith('.') ? item[1..] : item;
            if (ext.Length == 0 || ext.IndexOfAny(InvalidExtensionChars) >= 0)
                throw new ArgumentException($"Invalid extension: {item}");

            ext = ext.ToLowerInvariant();
            if (!parsedExtensions.Contains(ext)) parsedExtensions.Add(ext);
        }

        return new RescanFilter([.. parsedTypes], [.. parsedExtensions]);
    }

    /// <summary>
    /// The index query that selects at least every entry <see cref="Matches"/> accepts, so DroneDB
    /// filters out the rest: the types, and a single ASCII extension as a name pattern (the native
    /// pattern ignores case for ASCII letters only).
    /// </summary>
    public EntryQuery ToEntryQuery() => new(
        NamePattern: Extensions.Count == 1 && Extensions[0].All(char.IsAscii) ? "*." + Extensions[0] : null,
        Types: Types.Count > 0 ? Types : null);

    /// <summary>True if the rescan selects <paramref name="entry"/>. Directories never match.</summary>
    public bool Matches(EntrySummary entry)
    {
        if (entry.Type == EntryType.Directory || string.IsNullOrEmpty(entry.Path))
            return false;

        if (Types.Count > 0 && !Types.Contains(entry.Type))
            return false;

        // Same rule as the native filter: the path ends with "." + extension, ignoring case.
        return Extensions.Count == 0 || Extensions.Any(ext =>
            entry.Path.Length > ext.Length + 1 &&
            entry.Path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString() =>
        $"types: {TypesArgument ?? "all"}, extensions: {ExtensionsArgument ?? "all"}";

    private static IEnumerable<string> SplitItems(IEnumerable<string>? items) =>
        (items ?? []).Where(i => i != null).SelectMany(i => i.Split(','));
}
