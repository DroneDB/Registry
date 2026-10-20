#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Dataset folders whose index this process has already checked or initialized (singleton behind
/// the scoped <see cref="DdbManager"/>). A folder is forgotten when it is released for a delete,
/// move or wipe (<see cref="DdbManager.ReleaseConnections"/>), so the next open checks it again.
/// </summary>
public sealed class InitializedDatasetFolders
{
    private readonly ConcurrentDictionary<string, byte> _folders = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public bool Contains(string folder) => _folders.ContainsKey(Normalize(folder));

    public void Add(string folder) => _folders.TryAdd(Normalize(folder), 0);

    /// <summary>Forgets <paramref name="folder"/> and every dataset folder below it.</summary>
    public void Forget(string folder)
    {
        var root = Normalize(folder);
        var prefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var known in _folders.Keys.Where(k => k.Equals(root, comparison) || k.StartsWith(prefix, comparison)))
            _folders.TryRemove(known, out _);
    }

    private static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
