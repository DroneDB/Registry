#nullable enable
namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// Canonical identifiers of the built-in heavy tools, shared by every component that
/// has to recognize a task by its tool rather than by a local string literal.
/// </summary>
public static class HeavyToolIds
{
    /// <summary>Dataset build tool (matches the JobIndex.ToolId default).</summary>
    public const string Build = "build";
}
