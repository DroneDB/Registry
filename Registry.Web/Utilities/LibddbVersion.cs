#nullable enable
using System;
using System.Linq;
using System.Reflection;

namespace Registry.Web.Utilities;

/// <summary>The version of libddb (the native library), as the Hub and the update check report it.</summary>
public static class LibddbVersion
{
    private const string PinMetadataKey = "DdbVersion";

    /// <summary>
    /// The libddb version this Registry build is pinned to (the <c>DdbVersion</c> of the build), stamped into the
    /// assembly; null when the assembly was built without it.
    /// </summary>
    public static string? Pinned { get; } = typeof(LibddbVersion).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == PinMetadataKey)?.Value is { Length: > 0 } pinned ? pinned : null;

    /// <summary>
    /// The semantic version of what libddb answers (<c>"&lt;semver&gt; &lt;commit&gt;"</c>): the part before the first
    /// space; null for an empty answer.
    /// </summary>
    public static string? Semver(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var space = raw.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? raw : raw[..space];
    }
}
