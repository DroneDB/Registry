#nullable enable
using System.IO;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>The file names a tool derives from what the caller sent, for the artifact it produces.</summary>
public static class ToolFileNames
{
    /// <summary>
    /// <paramref name="name"/> without any directory component (a path traversal or an absolute path cannot
    /// leave the task's work folder), or <paramref name="fallback"/> when nothing is left.
    /// </summary>
    public static string Safe(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var safe = Path.GetFileName(name.Trim());
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }
}
