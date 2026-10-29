using System;
using System.IO;

namespace Registry.Web.Test.Support;

/// <summary>Files of the Registry checkout the tests check (compose files, templates).</summary>
public static class RepositoryFiles
{
    /// <summary>The repository root (holds Registry.sln), found from the test output folder; null outside a checkout.</summary>
    public static string? Root { get; } = FindRoot();

    /// <summary>The absolute path of <paramref name="relativePath"/> (from the repository root).</summary>
    /// <exception cref="InvalidOperationException">The tests do not run inside a Registry checkout.</exception>
    public static string PathOf(string relativePath) =>
        Path.Combine(Root ?? throw new InvalidOperationException(
            $"Registry.sln not found above {AppContext.BaseDirectory}: the test needs the repository checkout"),
            relativePath);

    private static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Registry.sln")))
                return dir.FullName;
        return null;
    }
}
